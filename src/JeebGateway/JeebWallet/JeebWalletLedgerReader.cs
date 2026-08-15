using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

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
/// The dev/CI fallback <see cref="IJeebWalletLedgerReader"/>: returns the empty
/// ledger page the mobile parser tolerates, used when
/// wallet-service is not the configured authority. Keeps the
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
