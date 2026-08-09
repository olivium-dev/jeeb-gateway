using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.JeebWallet;

/// <summary>
/// Holder-scoped ledger boundary. The wallet API is the authoritative source; callers never read
/// wallet tables directly through this interface.
/// </summary>
public interface IJeebWalletLedgerReader
{
    Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct);

    Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct) =>
        Task.FromResult<JeebWalletLedgerEntry?>(null);

    /// <summary>
    /// Cursor-aware generic wallet page. Existing page-number consumers keep using
    /// <see cref="ReadLedgerAsync"/>; migration probes can exercise the wallet cursor contract
    /// without changing the mobile response shape.
    /// </summary>
    async Task<JeebWalletLedgerReadPage> ReadLedgerPageAsync(
        Guid holderId, int page, int pageSize, string? cursor, string? type,
        DateOnly? from, DateOnly? to, CancellationToken ct) =>
        new(await ReadLedgerAsync(holderId, page, pageSize, type, from, to, ct), null);
}

public sealed record JeebWalletLedgerReadPage(
    IReadOnlyList<JeebWalletLedgerEntry> Items,
    string? NextCursor);

public sealed class WalletLedgerUnavailableException : Exception
{
    public WalletLedgerUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class WalletLedgerMigrationOptions
{
    public const string SectionName = "WalletLedgerMigration";

    /// <summary>
    /// Temporarily compare wallet API results with the legacy WalletPostgres projection. The
    /// comparison is observational only and never changes the API response.
    /// </summary>
    public bool ShadowCompareEnabled { get; set; }

    /// <summary>
    /// Temporarily compare wallet-backed settlement posts with an existing read-only gateway
    /// settlement-ledger row. Missing rows and mismatches are logged; wallet remains authoritative.
    /// </summary>
    public bool SettlementShadowCompareEnabled { get; set; }
}

/// <summary>
/// Authoritative generic wallet-service reader. Failures are surfaced so the BFF returns 502;
/// an upstream outage must never be misrepresented as an empty financial ledger.
/// </summary>
public sealed class WalletServiceJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    public const string HttpClientName = "wallet-ledger-api";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<WalletServiceJeebWalletLedgerReader> _log;

    public WalletServiceJeebWalletLedgerReader(
        IHttpClientFactory clients,
        ILogger<WalletServiceJeebWalletLedgerReader> log)
    {
        _clients = clients;
        _log = log;
    }

    public async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct)
    {
        var result = await ReadLedgerPageAsync(
            holderId, page, pageSize, cursor: null, type, from, to, ct);
        return result.Items;
    }

    public async Task<JeebWalletLedgerReadPage> ReadLedgerPageAsync(
        Guid holderId, int page, int pageSize, string? cursor, string? type,
        DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var safePage = Math.Max(page, 1);
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;
        var path = BuildListPath(holderId, safePage, safeSize, cursor, type, from, to);
        var response = await SendAsync(path, ct);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var pageResponse = await JsonSerializer.DeserializeAsync<WalletLedgerPage>(
                body, JsonOptions, ct);
            return new JeebWalletLedgerReadPage(
                pageResponse?.Items?.Select(Project).ToArray()
                    ?? Array.Empty<JeebWalletLedgerEntry>(),
                string.IsNullOrWhiteSpace(pageResponse?.NextCursor)
                    ? null
                    : pageResponse.NextCursor);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new WalletLedgerUnavailableException(
                "Wallet-service returned an invalid ledger response.", ex);
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct)
    {
        if (!Guid.TryParse(detailId, out var parsedDetailId)) return null;

        var client = _clients.CreateClient(HttpClientName);
        var path = $"Transaction/holder/{holderId:D}/ledger/{parsedDetailId:D}";
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new WalletLedgerUnavailableException("Wallet-service ledger read failed.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WalletLedgerUnavailableException("Wallet-service ledger read timed out.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            EnsureSuccess(response);
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var entry = await JsonSerializer.DeserializeAsync<WalletLedgerEntry>(
                    body, JsonOptions, ct);
                return entry is null ? null : Project(entry);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new WalletLedgerUnavailableException(
                    "Wallet-service returned an invalid ledger entry response.", ex);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
    {
        try
        {
            var response = await _clients.CreateClient(HttpClientName)
                .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            EnsureSuccess(response);
            return response;
        }
        catch (WalletLedgerUnavailableException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new WalletLedgerUnavailableException("Wallet-service ledger read failed.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WalletLedgerUnavailableException("Wallet-service ledger read timed out.", ex);
        }
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        _log.LogWarning(
            "Wallet-service ledger read failed with upstream status {StatusCode}.",
            (int)response.StatusCode);
        response.Dispose();
        throw new WalletLedgerUnavailableException(
            $"Wallet-service ledger read failed with status {(int)response.StatusCode}.");
    }

    private static string BuildListPath(
        Guid holderId, int page, int pageSize, string? cursor, string? type,
        DateOnly? from, DateOnly? to)
    {
        var query = new List<string>
        {
            $"page={page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add($"cursor={Uri.EscapeDataString(cursor.Trim())}");
        if (!string.IsNullOrWhiteSpace(type))
            query.Add($"type={Uri.EscapeDataString(type.Trim())}");
        if (from.HasValue)
            query.Add($"from={Uri.EscapeDataString(ToUtcStart(from.Value).ToString("O", CultureInfo.InvariantCulture))}");
        if (to.HasValue && to.Value != DateOnly.MaxValue)
            query.Add($"to={Uri.EscapeDataString(ToUtcStart(to.Value.AddDays(1)).ToString("O", CultureInfo.InvariantCulture))}");

        return $"Transaction/holder/{holderId:D}/ledger?{string.Join('&', query)}";
    }

    private static DateTimeOffset ToUtcStart(DateOnly value) =>
        new(value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero);

    private static JeebWalletLedgerEntry Project(WalletLedgerEntry source) => new()
    {
        Id = source.Id.ToString("D"),
        Type = source.Type ?? string.Empty,
        Amount = source.Amount,
        Sign = source.Sign,
        Ref = source.Reference ?? string.Empty,
        Ts = source.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };

    private sealed class WalletLedgerPage
    {
        [JsonPropertyName("items")]
        public List<WalletLedgerEntry>? Items { get; set; }

        [JsonPropertyName("nextCursor")]
        public string? NextCursor { get; set; }
    }

    private sealed class WalletLedgerEntry
    {
        public Guid Id { get; set; }
        public string? Type { get; set; }
        public decimal Amount { get; set; }
        public int Sign { get; set; }
        public string? Reference { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

/// <summary>Temporary read-only adapter used only as a migration shadow.</summary>
public interface IJeebWalletLedgerShadowReader
{
    Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct);

    Task<JeebWalletLedgerEntry?> ReadEntryAsync(Guid holderId, string detailId, CancellationToken ct);
}

public sealed class PostgresJeebWalletLedgerShadowReader : IJeebWalletLedgerShadowReader
{
    private readonly string _connectionString;

    public PostgresJeebWalletLedgerShadowReader(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Wallet shadow Postgres connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct)
    {
        var safePage = Math.Max(page, 1);
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;
        var walletIds = await ReadWalletIdsAsync(holderId, ct);
        if (walletIds.Count == 0) return Array.Empty<JeebWalletLedgerEntry>();

        const string sql = """
            SELECT
                d.txid::text,
                COALESCE(NULLIF(h.tag, ''), 'transaction'),
                d.amount,
                CASE WHEN d.destinationwalletid = ANY(@WalletIds) THEN 1 ELSE -1 END,
                COALESCE(NULLIF(h.summary, ''), NULLIF(h.notes, ''), ''),
                h.createdat
            FROM transactiondetails d
            JOIN transactionheader h ON h.txid = d.txheaderid
            WHERE (d.sourcewalletid = ANY(@WalletIds) OR d.destinationwalletid = ANY(@WalletIds))
              AND (@Type IS NULL OR COALESCE(NULLIF(h.tag, ''), 'transaction') = @Type)
              AND (@FromDate IS NULL OR h.createdat::date >= @FromDate)
              AND (@ToDate IS NULL OR h.createdat::date <= @ToDate)
            ORDER BY h.createdat DESC, d.txid DESC
            LIMIT @Limit OFFSET @Offset
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("WalletIds", walletIds.ToArray());
        cmd.Parameters.AddWithValue("Limit", safeSize);
        cmd.Parameters.AddWithValue("Offset", (safePage - 1) * safeSize);
        cmd.Parameters.Add(new NpgsqlParameter("Type", NpgsqlDbType.Text)
            { Value = string.IsNullOrWhiteSpace(type) ? DBNull.Value : type.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter("FromDate", NpgsqlDbType.Date)
            { Value = (object?)from ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ToDate", NpgsqlDbType.Date)
            { Value = (object?)to ?? DBNull.Value });
        return await ReadEntriesAsync(cmd, ct);
    }

    public async Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct)
    {
        if (!Guid.TryParse(detailId, out var parsedDetailId)) return null;
        var walletIds = await ReadWalletIdsAsync(holderId, ct);
        if (walletIds.Count == 0) return null;

        const string sql = """
            SELECT
                d.txid::text,
                COALESCE(NULLIF(h.tag, ''), 'transaction'),
                d.amount,
                CASE WHEN d.destinationwalletid = ANY(@WalletIds) THEN 1 ELSE -1 END,
                COALESCE(NULLIF(h.summary, ''), NULLIF(h.notes, ''), ''),
                h.createdat
            FROM transactiondetails d
            JOIN transactionheader h ON h.txid = d.txheaderid
            WHERE d.txid = @DetailId
              AND (d.sourcewalletid = ANY(@WalletIds) OR d.destinationwalletid = ANY(@WalletIds))
            LIMIT 1
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("WalletIds", walletIds.ToArray());
        cmd.Parameters.AddWithValue("DetailId", parsedDetailId);
        return (await ReadEntriesAsync(cmd, ct)).SingleOrDefault();
    }

    private async Task<List<Guid>> ReadWalletIdsAsync(Guid holderId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT walletid FROM wallets WHERE holderid = @HolderId", conn);
        cmd.Parameters.AddWithValue("HolderId", holderId);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadEntriesAsync(
        NpgsqlCommand cmd, CancellationToken ct)
    {
        var items = new List<JeebWalletLedgerEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var timestamp = reader.GetFieldValue<DateTime>(5);
            var utc = timestamp.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                : timestamp.ToUniversalTime();
            items.Add(new JeebWalletLedgerEntry
            {
                Id = reader.GetString(0),
                Type = reader.GetString(1),
                Amount = reader.GetDecimal(2),
                Sign = reader.GetInt32(3),
                Ref = reader.GetString(4),
                Ts = utc.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        return items;
    }
}

/// <summary>
/// Returns wallet API data and compares it with the legacy direct read. Shadow failures and
/// mismatches are observable but can never replace or suppress the authoritative response.
/// </summary>
public sealed class ShadowComparingJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    private readonly WalletServiceJeebWalletLedgerReader _primary;
    private readonly IJeebWalletLedgerShadowReader _shadow;
    private readonly ILogger<ShadowComparingJeebWalletLedgerReader> _log;

    public ShadowComparingJeebWalletLedgerReader(
        WalletServiceJeebWalletLedgerReader primary,
        IJeebWalletLedgerShadowReader shadow,
        ILogger<ShadowComparingJeebWalletLedgerReader> log)
    {
        _primary = primary;
        _shadow = shadow;
        _log = log;
    }

    public async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct)
    {
        var primary = await _primary.ReadLedgerAsync(holderId, page, pageSize, type, from, to, ct);
        try
        {
            var shadow = await _shadow.ReadLedgerAsync(holderId, page, pageSize, type, from, to, ct);
            LogComparison(holderId, "page", primary, shadow);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Wallet ledger shadow read failed for holder {HolderId}.", holderId);
        }
        return primary;
    }

    public async Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct)
    {
        var primary = await _primary.ReadEntryAsync(holderId, detailId, ct);
        try
        {
            var shadow = await _shadow.ReadEntryAsync(holderId, detailId, ct);
            LogComparison(
                holderId,
                "detail",
                primary is null ? Array.Empty<JeebWalletLedgerEntry>() : new[] { primary },
                shadow is null ? Array.Empty<JeebWalletLedgerEntry>() : new[] { shadow });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Wallet ledger shadow detail read failed for holder {HolderId}.", holderId);
        }
        return primary;
    }

    public async Task<JeebWalletLedgerReadPage> ReadLedgerPageAsync(
        Guid holderId, int page, int pageSize, string? cursor, string? type,
        DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var primary = await _primary.ReadLedgerPageAsync(
            holderId, page, pageSize, cursor, type, from, to, ct);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            // The legacy projection has no compatible opaque cursor. Skipping is explicit and
            // never falls back to a page-number comparison that could report a false mismatch.
            _log.LogInformation(
                "WalletLedgerShadowSkipped holder={HolderId} scope=cursor reason=legacy_cursor_unsupported",
                holderId);
            return primary;
        }

        try
        {
            var shadow = await _shadow.ReadLedgerAsync(
                holderId, page, pageSize, type, from, to, ct);
            LogComparison(holderId, "page", primary.Items, shadow);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Wallet ledger shadow read failed for holder {HolderId}.", holderId);
        }
        return primary;
    }

    private void LogComparison(
        Guid holderId,
        string scope,
        IReadOnlyList<JeebWalletLedgerEntry> primary,
        IReadOnlyList<JeebWalletLedgerEntry> shadow)
    {
        var primaryDigest = Digest(primary);
        var shadowDigest = Digest(shadow);
        if (primary.Count == shadow.Count
            && string.Equals(primaryDigest, shadowDigest, StringComparison.Ordinal))
        {
            _log.LogInformation(
                "WalletLedgerShadowMatch holder={HolderId} scope={Scope} count={Count} digest={Digest}",
                holderId, scope, primary.Count, primaryDigest);
            return;
        }

        _log.LogWarning(
            "WalletLedgerShadowMismatch holder={HolderId} scope={Scope} primaryCount={PrimaryCount} " +
            "shadowCount={ShadowCount} primaryDigest={PrimaryDigest} shadowDigest={ShadowDigest}",
            holderId, scope, primary.Count, shadow.Count, primaryDigest, shadowDigest);
    }

    private static string Digest(IEnumerable<JeebWalletLedgerEntry> items)
    {
        var canonical = string.Join('\n', items.Select(item => string.Join('|',
            item.Id,
            item.Type,
            item.Amount.ToString(CultureInfo.InvariantCulture),
            item.Sign.ToString(CultureInfo.InvariantCulture),
            item.Ref,
            NormalizeTimestamp(item.Ts))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string NormalizeTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : value;
}
