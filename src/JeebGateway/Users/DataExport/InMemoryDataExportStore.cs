using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// MVP in-memory implementation of <see cref="IDataExportStore"/>.
/// Mirrors the lifecycle the Postgres-backed store will implement so the
/// controller, processor, and tests can share one seam.
///
/// Concurrency strategy:
///   * a single write lock protects the lifecycle transitions — the
///     queued → processing claim must be atomic so two processors never
///     pick up the same row.
///   * the dictionary is concurrent for read-heavy access (controller
///     GETs, token lookups).
/// </summary>
public class InMemoryDataExportStore : IDataExportStore
{
    private readonly ConcurrentDictionary<string, DataExportRequest> _byId = new();
    private readonly object _writeLock = new();
    private readonly TimeProvider _clock;
    private readonly DataExportOptions _options;

    public InMemoryDataExportStore(TimeProvider clock, Microsoft.Extensions.Options.IOptions<DataExportOptions> options)
    {
        _clock = clock;
        _options = options.Value;
    }

    public Task<DataExportRequest> RequestAsync(string userId, string format, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        lock (_writeLock)
        {
            var open = _byId.Values.FirstOrDefault(r =>
                string.Equals(r.UserId, userId, StringComparison.Ordinal)
                && DataExportStatus.IsOpen(r.Status));
            if (open is not null)
            {
                return Task.FromResult(open);
            }

            var record = new DataExportRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Status = DataExportStatus.Queued,
                Format = format,
                RequestedAt = now,
                DueBy = now + _options.Sla
            };
            _byId[record.Id] = record;
            return Task.FromResult(record);
        }
    }

    public Task<DataExportRequest?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        var latest = _byId.Values
            .Where(r => string.Equals(r.UserId, userId, StringComparison.Ordinal))
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<DataExportRequest?> GetByDownloadTokenAsync(string token, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult<DataExportRequest?>(null);
        }

        var match = _byId.Values.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.DownloadToken)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(r.DownloadToken!),
                System.Text.Encoding.UTF8.GetBytes(token)));

        if (match is null) return Task.FromResult<DataExportRequest?>(null);
        if (match.Status != DataExportStatus.Ready) return Task.FromResult<DataExportRequest?>(null);
        if (match.LinkExpiresAt is { } exp && exp <= now)
        {
            return Task.FromResult<DataExportRequest?>(null);
        }
        return Task.FromResult<DataExportRequest?>(match);
    }

    public Task<DataExportRequest?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var next = _byId.Values
                .Where(r => r.Status == DataExportStatus.Queued)
                .OrderBy(r => r.RequestedAt)
                .FirstOrDefault();
            if (next is null) return Task.FromResult<DataExportRequest?>(null);

            next.Status = DataExportStatus.Processing;
            next.StartedAt = now;
            return Task.FromResult<DataExportRequest?>(next);
        }
    }

    public Task<string> MarkReadyAsync(
        string exportId,
        byte[] payload,
        string contentType,
        DateTimeOffset now,
        TimeSpan linkValidity,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(exportId, out var record))
            {
                throw new InvalidOperationException($"Data export '{exportId}' not found.");
            }
            if (record.Status != DataExportStatus.Processing)
            {
                throw new InvalidOperationException(
                    $"Data export '{exportId}' is in status '{record.Status}', expected 'processing'.");
            }

            var token = MintToken();
            record.Status = DataExportStatus.Ready;
            record.ReadyAt = now;
            record.DownloadToken = token;
            record.LinkExpiresAt = now + linkValidity;
            record.Payload = payload;
            record.PayloadContentType = contentType;
            record.PayloadSizeBytes = payload.LongLength;
            return Task.FromResult(token);
        }
    }

    public Task MarkFailedAsync(string exportId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(exportId, out var record)) return Task.CompletedTask;
            record.Status = DataExportStatus.Failed;
            record.FailedAt = now;
            record.FailureReason = reason;
            record.Payload = null;
            return Task.CompletedTask;
        }
    }

    public Task<bool> MarkDeliveredAsync(string exportId, DateTimeOffset now, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(exportId, out var record))
            {
                return Task.FromResult(false);
            }
            if (record.Status != DataExportStatus.Ready)
            {
                return Task.FromResult(false);
            }

            record.Status = DataExportStatus.Delivered;
            record.DeliveredAt = now;
            // PII deliberately dropped on delivery — the audit row stays.
            record.Payload = null;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// 256 bits of cryptographic randomness, URL-safe base64-encoded.
    /// Equal in length to the auth-service refresh tokens — long enough
    /// that a brute-force download attempt is computationally infeasible.
    /// </summary>
    internal static string MintToken()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
