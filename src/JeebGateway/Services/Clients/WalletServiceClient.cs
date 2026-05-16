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
}

/// <summary>
/// MVP fallback used until wallet-service is reachable in every Jeeb
/// environment (no BaseUrl configured). Records the request in memory so
/// settlement still completes end-to-end and integration tests can assert
/// the ledger payload without a downstream dependency.
///
/// Registered as the default <see cref="IWalletServiceClient"/> binding;
/// the HTTP-backed <see cref="WalletServiceClient"/> takes over once
/// <c>Services:Wallet:BaseUrl</c> is set.
/// </summary>
public sealed class InMemoryWalletServiceClient : IWalletServiceClient
{
    private readonly List<LedgerEntryRequest> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<LedgerEntryRequest> Entries
    {
        get
        {
            lock (_lock) return _entries.ToArray();
        }
    }

    public Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            _entries.Add(request);
        }

        return Task.FromResult(new LedgerEntryResponse
        {
            LedgerEntryId = $"ledger-{request.IdempotencyKey}",
            PostedAt = DateTimeOffset.UtcNow,
        });
    }
}
