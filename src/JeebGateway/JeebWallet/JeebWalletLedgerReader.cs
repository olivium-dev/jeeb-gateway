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
/// Holder-scoped ledger boundary. Which implementation serves is chosen at composition time by
/// WalletLedgerMigration:Authority — "postgres" (dev/CI default) or "wallet-api" (Production).
/// </summary>
public interface IJeebWalletLedgerReader
{
    Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct);

    Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct) =>
        Task.FromResult<JeebWalletLedgerEntry?>(null);
}

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
    public const string AuthorityPostgres = "postgres";
    public const string AuthorityWalletApi = "wallet-api";

    /// <summary>
    /// Also read the non-authoritative source and compare. Observational only: a shadow failure or
    /// mismatch is logged and can never change, delay past its own timeout, or suppress the response.
    /// </summary>
    public bool ShadowCompareEnabled { get; set; }

    /// <summary>
    /// Which source SERVES <c>GET /v1/jeeb/wallet/ledger</c>: "postgres" (default, today's live
    /// behaviour) or "wallet-api". Flip only after a clean WalletLedgerShadowMismatch window.
    /// </summary>
    public string Authority { get; set; } = AuthorityPostgres;

    public bool WalletApiIsAuthoritative => string.Equals(
        Authority?.Trim(), AuthorityWalletApi, StringComparison.OrdinalIgnoreCase);
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
        var safePage = Math.Max(page, 1);
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;
        var path = BuildListPath(holderId, safePage, safeSize, type, from, to);
        var response = await SendAsync(path, ct);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var pageResponse = await JsonSerializer.DeserializeAsync<WalletLedgerPage>(
                body, JsonOptions, ct);
            return pageResponse?.Items?.Select(Project).ToArray()
                ?? Array.Empty<JeebWalletLedgerEntry>();
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
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to)
    {
        var query = new List<string>
        {
            $"page={page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
        };
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
        // UtcDateTime, not ToUniversalTime(): a DateTimeOffset renders "+00:00" where the postgres
        // authority renders "Z", and the served wire format must not change when Authority flips.
        Ts = source.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
    };

    private sealed class WalletLedgerPage
    {
        [JsonPropertyName("items")]
        public List<WalletLedgerEntry>? Items { get; set; }
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

/// <summary>
/// Postgres-backed <see cref="IJeebWalletLedgerReader"/>. Reads the wallet DB
/// directly via the connection string at <c>WalletPostgres:ConnectionString</c>.
/// Registered only when that key is configured; otherwise the controller falls back
/// to the correctly-shaped empty page (no behaviour regression in dev/CI).
/// </summary>
public sealed class PostgresJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    // The holder's transactions = every transactiondetails row whose source OR
    // destination wallet belongs to the holder. sign is derived per-row: +1 when
    // the holder is the DESTINATION (credit / money in), -1 when the SOURCE
    // (debit / money out). type/ref/ts come from the transaction header.
    //
    // PP-8 server-side filters (all OPTIONAL, applied in this same read path — no
    // extra round-trip, no new table). Each is a null-guarded predicate so an absent
    // filter binds DBNull and the WHERE reduces to the exact pre-PP-8 query (backward
    // compatible): type matches the SAME projected string surfaced as each row's `type`
    // (so an unknown value is a natural empty result, not an error); from/to compare the
    // UTC calendar date and are BOTH inclusive (>= from-day, <= to-day / whole to-day).
    internal const string PageSql = """
        SELECT
            d.txid::text                                         AS id,
            COALESCE(NULLIF(h.tag, ''), 'transaction')           AS type,
            d.amount                                             AS amount,
            CASE WHEN d.destinationwalletid = ANY(@WalletIds) THEN 1 ELSE -1 END AS sign,
            COALESCE(NULLIF(h.summary, ''), NULLIF(h.notes, ''), '') AS ref,
            h.createdat                                          AS ts
        FROM transactiondetails d
        JOIN transactionheader  h ON h.txid = d.txheaderid
        WHERE (d.sourcewalletid = ANY(@WalletIds)
            OR d.destinationwalletid = ANY(@WalletIds))
          AND (@Type IS NULL OR COALESCE(NULLIF(h.tag, ''), 'transaction') = @Type)
          AND (@FromDate IS NULL OR h.createdat::date >= @FromDate)
          AND (@ToDate   IS NULL OR h.createdat::date <= @ToDate)
        -- DESC/DESC tie-break matches wallet-service, whose keyset cursor
        -- (CreatedAt, TxId) < (…) is only correct in that order.
        ORDER BY h.createdat DESC, d.txid DESC
        LIMIT @Limit OFFSET @Offset
        """;

    /// <summary>Same projection and ownership predicate as <see cref="PageSql"/>, one row by id.</summary>
    internal const string EntrySql = """
        SELECT
            d.txid::text                                         AS id,
            COALESCE(NULLIF(h.tag, ''), 'transaction')           AS type,
            d.amount                                             AS amount,
            CASE WHEN d.destinationwalletid = ANY(@WalletIds) THEN 1 ELSE -1 END AS sign,
            COALESCE(NULLIF(h.summary, ''), NULLIF(h.notes, ''), '') AS ref,
            h.createdat                                          AS ts
        FROM transactiondetails d
        JOIN transactionheader  h ON h.txid = d.txheaderid
        WHERE d.txid = @DetailId
          AND (d.sourcewalletid = ANY(@WalletIds)
            OR d.destinationwalletid = ANY(@WalletIds))
        LIMIT 1
        """;

    private readonly string _connectionString;
    private readonly ILogger<PostgresJeebWalletLedgerReader> _log;

    public PostgresJeebWalletLedgerReader(string connectionString, ILogger<PostgresJeebWalletLedgerReader> log)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Wallet Postgres connection string must be configured.", nameof(connectionString));
        _connectionString = connectionString;
        _log = log;
    }

    public async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;
        var offset = PageOffset(safePage, safeSize);

        // Normalize an empty/whitespace type to "no filter" (defensive — the controller
        // already collapses it) so ?type= behaves identically to an absent param.
        var typeFilter = string.IsNullOrWhiteSpace(type) ? null : type.Trim();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // 1) Resolve the holder's wallet ids (the join key into the transaction tables).
            var walletIds = await ReadWalletIdsAsync(conn, holderId, ct);
            if (walletIds.Count == 0) return Array.Empty<JeebWalletLedgerEntry>();

            // 2) Page the holder's transactions.
            await using var cmd = new NpgsqlCommand(PageSql, conn);
            cmd.Parameters.AddWithValue("WalletIds", walletIds.ToArray());
            cmd.Parameters.AddWithValue("Limit", safeSize);
            cmd.Parameters.AddWithValue("Offset", offset);
            // Null-guarded PP-8 filters — typed so DBNull resolves the (@Param IS NULL) branch.
            cmd.Parameters.Add(new NpgsqlParameter("Type", NpgsqlDbType.Text) { Value = (object?)typeFilter ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("FromDate", NpgsqlDbType.Date) { Value = (object?)from ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ToDate", NpgsqlDbType.Date) { Value = (object?)to ?? DBNull.Value });

            var items = new List<JeebWalletLedgerEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new JeebWalletLedgerEntry
                {
                    Id = reader.GetString(0),
                    Type = reader.GetString(1),
                    // JEBV4-49 (M4): keep money as decimal end-to-end — read the
                    // NUMERIC column straight into the decimal DTO, no (double) cast
                    // (a double cast can lose integer precision past 2^53 and
                    // reintroduce fractional artifacts on large LBP amounts).
                    Amount = reader.GetDecimal(2),
                    Sign = reader.GetInt32(3),
                    Ref = reader.GetString(4),
                    Ts = FormatUtcTimestamp(reader.GetFieldValue<DateTime>(5)),
                });
            }
            return items;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled read produced no rows — degrading it to [] would report an empty
            // ledger as fact, and read as a shadow MISMATCH once this reader is the shadow.
            throw;
        }
        catch (Exception ex)
        {
            // Graceful degrade (ADR-0001): the ledger is a non-critical read; a DB blip
            // returns the empty page the mobile parser tolerates rather than a 5xx.
            _log.LogWarning(ex, "wallet ledger read for holder {HolderId} degraded to empty", holderId);
            return Array.Empty<JeebWalletLedgerEntry>();
        }
    }

    /// <summary>
    /// Single ledger row, mirroring wallet-service <c>GetHolderLedgerEntry</c>: same projection,
    /// holder-ownership on EITHER side of the transfer, same +1-on-destination sign.
    /// </summary>
    public async Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct)
    {
        if (!Guid.TryParse(detailId, out var parsedDetailId)) return null;

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var walletIds = await ReadWalletIdsAsync(conn, holderId, ct);
            if (walletIds.Count == 0) return null;

            await using var cmd = new NpgsqlCommand(EntrySql, conn);
            cmd.Parameters.AddWithValue("WalletIds", walletIds.ToArray());
            cmd.Parameters.AddWithValue("DetailId", parsedDetailId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new JeebWalletLedgerEntry
            {
                Id = reader.GetString(0),
                Type = reader.GetString(1),
                Amount = reader.GetDecimal(2),
                Sign = reader.GetInt32(3),
                Ref = reader.GetString(4),
                Ts = FormatUtcTimestamp(reader.GetFieldValue<DateTime>(5)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // null means "no such row for this holder" and the controller turns it into 404.
            // A wallet-DB outage is not that verdict, and wallet-api 502s here — so must this.
            throw new WalletLedgerUnavailableException("Wallet ledger detail read failed.", ex);
        }
    }

    /// <summary>
    /// <c>h.createdat</c> is <c>timestamp</c> WITHOUT time zone storing UTC, so Npgsql yields
    /// Kind=Unspecified: label it UTC, never ToUniversalTime() (that subtracts the host offset).
    /// </summary>
    internal static string FormatUtcTimestamp(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// 64-bit: int.MaxValue * 200 wraps to a negative OFFSET, which Postgres rejects (22023)
    /// and the caller's graceful degrade would then serve as an empty financial ledger.
    /// </summary>
    internal static long PageOffset(int safePage, int safeSize) => (long)(safePage - 1) * safeSize;

    private static async Task<List<Guid>> ReadWalletIdsAsync(NpgsqlConnection conn, Guid holderId, CancellationToken ct)
    {
        const string walletSql = "SELECT walletid FROM wallets WHERE holderid = @HolderId";
        await using var cmd = new NpgsqlCommand(walletSql, conn);
        cmd.Parameters.AddWithValue("HolderId", holderId);

        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }
}

/// <summary>
/// The dev/CI fallback <see cref="IJeebWalletLedgerReader"/>: returns the empty
/// ledger page the mobile parser tolerates, used when
/// <c>WalletPostgres:ConnectionString</c> is unset (no wallet DB to read). Keeps the
/// controller's dependency satisfiable in tests / local runs without Postgres —
/// identical to the pre-fix behaviour, so there is no regression when unconfigured.
/// </summary>
public sealed class NullJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JeebWalletLedgerEntry>>(Array.Empty<JeebWalletLedgerEntry>());
}

/// <summary>
/// Serves the primary reader and compares it against a non-serving shadow (roles assigned by the
/// caller from Authority). Shadow failures and mismatches can never alter the served response.
/// </summary>
public sealed class ShadowComparingJeebWalletLedgerReader : IJeebWalletLedgerReader
{
    /// <summary>Budget for the detached shadow read; deliberately a const, not a config key (G-22).</summary>
    private static readonly TimeSpan ShadowReadBudget = TimeSpan.FromSeconds(5);

    /// <summary>Detached reads are unbounded by the request; cap them so a stalling shadow
    /// cannot accumulate RPS × budget concurrent calls onto an already-degraded upstream.</summary>
    internal const int MaxConcurrentShadowReads = 8;

    private readonly SemaphoreSlim _shadowSlots = new(MaxConcurrentShadowReads);
    private readonly IJeebWalletLedgerReader _primary;
    private readonly IJeebWalletLedgerReader _shadow;
    private readonly ILogger<ShadowComparingJeebWalletLedgerReader> _log;

    public ShadowComparingJeebWalletLedgerReader(
        IJeebWalletLedgerReader primary,
        IJeebWalletLedgerReader shadow,
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
        CompareDetached(holderId, "page", primary, shadowCt =>
            _shadow.ReadLedgerAsync(holderId, page, pageSize, type, from, to, shadowCt));
        return primary;
    }

    public async Task<JeebWalletLedgerEntry?> ReadEntryAsync(
        Guid holderId, string detailId, CancellationToken ct)
    {
        var primary = await _primary.ReadEntryAsync(holderId, detailId, ct);
        CompareDetached(holderId, "detail", AsList(primary), async shadowCt =>
            AsList(await _shadow.ReadEntryAsync(holderId, detailId, shadowCt)));
        return primary;
    }

    /// <summary>
    /// Observational by construction: the shadow runs off the request (its own token and budget,
    /// never the caller's) so it cannot delay, alter or fault the already-computed response.
    /// </summary>
    private void CompareDetached(
        Guid holderId,
        string scope,
        IReadOnlyList<JeebWalletLedgerEntry> primary,
        Func<CancellationToken, Task<IReadOnlyList<JeebWalletLedgerEntry>>> readShadow)
    {
        if (!_shadowSlots.Wait(0))
        {
            _log.LogInformation(
                "WalletLedgerShadowSkipped holder={HolderId} scope={Scope} reason=slots-saturated",
                holderId, scope);
            return;
        }

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(ShadowReadBudget);
            try
            {
                LogComparison(holderId, scope, primary, await readShadow(cts.Token));
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex, "Wallet ledger shadow {Scope} read failed for holder {HolderId}.", scope, holderId);
            }
            finally
            {
                _shadowSlots.Release();
            }
        }, CancellationToken.None);
    }

    private static IReadOnlyList<JeebWalletLedgerEntry> AsList(JeebWalletLedgerEntry? entry) =>
        entry is null ? Array.Empty<JeebWalletLedgerEntry>() : new[] { entry };

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

    /// <summary>
    /// Hashes the served bytes verbatim — no timestamp canonicalisation, or a wire-format drift
    /// between the two authorities would digest identically and the flip window could not see it.
    /// </summary>
    internal static string Digest(IEnumerable<JeebWalletLedgerEntry> items)
    {
        var canonical = string.Join('\n', items.Select(item => string.Join('|',
            item.Id,
            item.Type,
            item.Amount.ToString(CultureInfo.InvariantCulture),
            item.Sign.ToString(CultureInfo.InvariantCulture),
            item.Ref,
            item.Ts)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
