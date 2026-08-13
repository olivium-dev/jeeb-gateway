using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

// gwdbx W1-06 — dual-writes the GDPR export lifecycle to state-service /v1/work-items behind
// FeatureFlags:DataExportMode. data_exports stays authoritative; a mirror failure never throws.
public sealed class MirroringDataExportStore : IDataExportStore
{
    // Application scope the state-service credential grants this gateway.
    public const string Application = "jeeb-gateway";

    public const string WorkKind = "data-export";

    private const string LeasedStatus = "leased";

    // Bounded so a pathological caller cannot grow the subject map without limit; a miss only
    // costs a skipped mirror leg (the local row stays authoritative either way).
    private const int SubjectMapCapacity = 1024;

    private static readonly TimeSpan MirrorBudget = TimeSpan.FromMilliseconds(3000);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, ExportSubject> _subjects = new(StringComparer.Ordinal);

    private readonly IDataExportStore _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<MirroringDataExportStore> _log;

    public MirroringDataExportStore(
        IDataExportStore inner,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<MirroringDataExportStore> log)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _mode = mode;
        _log = log;
    }

    // G-15 — one open export per user upstream, reproducing uq_data_exports_user_open.
    public static string IdempotencyKeyFor(string userId) => "data-export:" + userId;

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private bool Mirroring => _mode.CurrentValue.DataExport >= GwdbxMigrationPhase.DualWriteLocalRead;

    public async Task<DataExportRequest> RequestAsync(string userId, string format, CancellationToken ct)
    {
        // AUTHORITATIVE first — the returned row is the data_exports row.
        var row = await _inner.RequestAsync(userId, format, ct);
        Remember(row.Id, new ExportSubject(row.UserId, null));

        if (Mirroring)
        {
            await MirrorRequestAsync(row, ct);
        }
        return row;
    }

    // Reads stay local at dual-write-local-read; the upstream read cutover is W1-08.
    public Task<DataExportRequest?> GetLatestForUserAsync(string userId, CancellationToken ct) =>
        _inner.GetLatestForUserAsync(userId, ct);

    public async Task<DataExportRequest?> GetByDownloadTokenAsync(
        string token, DateTimeOffset now, CancellationToken ct)
    {
        var row = await _inner.GetByDownloadTokenAsync(token, now, ct);
        if (row is not null)
        {
            // The raw token is needed only to derive the consume hash on delivery.
            Remember(row.Id, new ExportSubject(row.UserId, token));
        }
        return row;
    }

    // The local queue stays the claim authority in W1-06: an upstream claim is due-scoped and
    // would lease other subjects' items. The upstream claim rail lands with the W1-08 cutover.
    public async Task<DataExportRequest?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        var row = await _inner.ClaimNextAsync(now, ct);
        if (row is not null)
        {
            Remember(row.Id, new ExportSubject(row.UserId, null));
        }
        return row;
    }

    public async Task<string> MarkReadyAsync(
        string exportId,
        byte[] payload,
        string contentType,
        DateTimeOffset now,
        TimeSpan linkValidity,
        CancellationToken ct)
    {
        var token = await _inner.MarkReadyAsync(exportId, payload, contentType, now, linkValidity, ct);
        if (Mirroring)
        {
            await StageArtifactAsync(exportId, payload, token, now + linkValidity, ct);
        }
        return token;
    }

    // The upstream fail rail needs a lease (W1-09 owns it); the local row stays authoritative.
    public async Task MarkFailedAsync(string exportId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await _inner.MarkFailedAsync(exportId, reason, now, ct);
        Forget(exportId);
    }

    public async Task<bool> MarkDeliveredAsync(string exportId, DateTimeOffset now, CancellationToken ct)
    {
        var delivered = await _inner.MarkDeliveredAsync(exportId, now, ct);
        var subject = Forget(exportId);

        // Only the winner of the local single-use race consumes upstream; the upstream CAS
        // is the second, independent guard (two racing downloads -> exactly one 200).
        if (delivered && Mirroring && subject is { Token: not null })
        {
            await MirrorConsumeAsync(exportId, subject, ct);
        }
        else if (delivered && Mirroring)
        {
            _log.LogDebug(
                "data-export mirror: download token for export {ExportId} was resolved elsewhere; " +
                "upstream consume skipped (the local row is delivered).",
                exportId);
        }
        return delivered;
    }

    private async Task MirrorRequestAsync(DataExportRequest row, CancellationToken ct)
    {
        try
        {
            using var budget = NewBudget(ct);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();

            // Payload carries NO PII: the export bytes live encrypted on cdn, never here.
            var body = new WorkItemCreateRequestV1
            {
                Application = Application,
                Kind = WorkKind,
                SubjectRef = row.UserId,
                Payload = JsonSerializer.SerializeToElement(
                    new { exportId = row.Id, format = row.Format }, Json),
                // The 72-hour SLA deadline, stamped at queue time (DataExportOptions.Sla).
                DueAt = row.DueBy,
            };
            await client.CreateWorkItemAsync(body, IdempotencyKeyFor(row.UserId), budget.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "data-export mirror: create work item failed for export {ExportId} (key {Key}); " +
                "the local row is durable and the W1-07 relay replays the same key.",
                row.Id, IdempotencyKeyFor(row.UserId));
        }
    }

    // Ciphertext is uploaded only when the upstream item is already leased, because complete()
    // needs that lease: an artifact is never uploaded without a home for its objectRef (G-20).
    private async Task StageArtifactAsync(
        string exportId, byte[] payload, string token, DateTimeOffset artifactExpiresAt, CancellationToken ct)
    {
        if (!_subjects.TryGetValue(exportId, out var subject))
        {
            _log.LogWarning(
                "data-export mirror: no subject known for export {ExportId}; artifact staging skipped.",
                exportId);
            return;
        }

        try
        {
            using var budget = NewBudget(ct);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();

            var item = await client.GetLatestWorkItemAsync(
                Application, WorkKind, subject.UserId, budget.Token);
            if (item?.LeaseToken is not { } lease
                || !string.Equals(item.Status, LeasedStatus, StringComparison.Ordinal))
            {
                _log.LogDebug(
                    "data-export mirror: work item for export {ExportId} is not leased (status {Status}); " +
                    "artifact staging waits for the W1-08 read cutover.",
                    exportId, item?.Status ?? "none");
                return;
            }

            var uploader = scope.ServiceProvider.GetRequiredService<IDataExportArtifactUploader>();
            var artifact = await uploader.UploadAsync(payload, budget.Token);

            await client.CompleteWorkItemAsync(
                item.WorkItemId,
                new WorkCompleteRequestV1
                {
                    LeaseToken = lease,
                    ExpectedVersion = item.Version,
                    ArtifactRef = artifact.ObjectRef,
                    ArtifactExpiresAt = artifactExpiresAt,
                    DownloadTokenHash = HashToken(token),
                    Result = JsonSerializer.SerializeToElement(
                        new { ciphertextBytes = artifact.CiphertextBytes }, Json),
                },
                budget.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "data-export mirror: artifact staging failed for export {ExportId}; the local ready row " +
                "is durable and still serves the download.",
                exportId);
        }
    }

    private async Task MirrorConsumeAsync(string exportId, ExportSubject subject, CancellationToken ct)
    {
        try
        {
            using var budget = NewBudget(ct);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();

            var item = await client.GetLatestWorkItemAsync(
                Application, WorkKind, subject.UserId, budget.Token);
            if (item is null || string.IsNullOrEmpty(item.ArtifactRef))
            {
                _log.LogDebug(
                    "data-export mirror: no completed work item to consume for export {ExportId}.", exportId);
                return;
            }

            await client.ConsumeWorkItemAsync(
                item.WorkItemId,
                new WorkConsumeRequestV1
                {
                    Application = Application,
                    DownloadTokenHash = HashToken(subject.Token!),
                    ExpectedVersion = item.Version,
                },
                budget.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "data-export mirror: consume failed for export {ExportId}; the local row is already " +
                "delivered and the token is spent.",
                exportId);
        }
    }

    private CancellationTokenSource NewBudget(CancellationToken ct)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(MirrorBudget);
        return budget;
    }

    private void Remember(string exportId, ExportSubject subject)
    {
        if (_subjects.Count >= SubjectMapCapacity && !_subjects.ContainsKey(exportId))
        {
            return;
        }
        // A later leg without the token (e.g. a retried POST returning the open row) must not
        // erase a token an earlier download lookup resolved.
        _subjects.AddOrUpdate(
            exportId,
            subject,
            (_, existing) => subject with { Token = subject.Token ?? existing.Token });
    }

    private ExportSubject? Forget(string exportId) =>
        _subjects.TryRemove(exportId, out var subject) ? subject : null;

    private sealed record ExportSubject(string UserId, string? Token);
}
