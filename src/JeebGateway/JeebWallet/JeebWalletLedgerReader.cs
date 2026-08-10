using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

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
}

public sealed class WalletLedgerUnavailableException : Exception
{
    public WalletLedgerUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
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
        Ts = source.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
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

/// <summary>Temporary read-only adapter used only as a migration shadow.</summary>
public interface IJeebWalletLedgerShadowReader
{
    Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
        Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
        CancellationToken ct);

    Task<JeebWalletLedgerEntry?> ReadEntryAsync(Guid holderId, string detailId, CancellationToken ct);
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
