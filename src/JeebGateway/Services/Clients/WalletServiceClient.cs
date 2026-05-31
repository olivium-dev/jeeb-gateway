using System.Net.Http.Json;
using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed <see cref="IWalletServiceClient"/>. The named
/// "wallet" HttpClient is registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/> with the
/// org-standard resilience pipeline (retry / circuit-breaker / timeout),
/// so this class never has to think about those concerns.
///
/// The wire shape mirrors the placeholder OpenAPI under
/// <c>contracts/wallet-service.openapi.json</c>; replace this hand-coded
/// implementation with the NSwag-generated client during the
/// T-backend-bff-wallet follow-up.
/// </summary>
public sealed class WalletServiceClient : IWalletServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public WalletServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "ledger/entries")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        // Idempotency-Key is the canonical HTTP header for replay-safe
        // mutations. wallet-service trusts the gateway to supply a stable
        // key per logical settlement (we use the settlement id).
        message.Headers.Add("Idempotency-Key", request.IdempotencyKey);

        using var response = await _http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<LedgerEntryResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException("wallet-service returned an empty ledger response.");
        }
        return payload;
    }

    /// <inheritdoc/>
    public async Task<SystemWalletResponse?> GetSystemWalletAsync(CancellationToken ct)
    {
        // wallet-service route: GET /system-wallet (absolute path — not under a
        // controller prefix, defined directly on the action with [HttpGet("/system-wallet")]).
        using var response = await _http.GetAsync("system-wallet", ct);

        // 404 means the system wallet holder hasn't been seeded yet — not a
        // fatal error at the gateway layer; callers receive null and decide.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SystemWalletResponse>(JsonOptions, ct);
    }

    /// <inheritdoc/>
    public async Task<SystemWalletResponse?> GetHolderWalletsAsync(string holderId, CancellationToken ct)
    {
        // wallet-service route: GET /Wallet/holder/{holderId}/wallets.
        // When the holder is unknown the upstream returns 200 with an empty
        // object ({}) rather than 404, so an absent WalletHolder means
        // "no wallet provisioned" — we surface that as null.
        using var response = await _http.GetAsync(
            $"Wallet/holder/{Uri.EscapeDataString(holderId)}/wallets", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SystemWalletResponse>(JsonOptions, ct);
        return payload?.WalletHolder is null ? null : payload;
    }
}
