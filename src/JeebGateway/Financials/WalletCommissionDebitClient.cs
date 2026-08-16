using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.JeebWallet;

namespace JeebGateway.Financials;

/// <summary>Raised for every wallet-service fault on the commission-debit path; <see cref="StatusCode"/>
/// is null for a transport/timeout fault, which the collector must treat as AMBIGUOUS.</summary>
public sealed class WalletCommissionDebitException : Exception
{
    public WalletCommissionDebitException(string message, HttpStatusCode? statusCode, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }

    /// <summary>A deterministic upstream rejection: the money did NOT move on this call.</summary>
    public bool IsDeterministicRejection =>
        StatusCode is { } s && (int)s >= 400 && (int)s < 500;
}

/// <summary>
/// O1 — the narrow wallet-service surface the commission debit needs. Hand-rolled because the
/// generated <c>ServiceWalletClient</c> carries no <c>Idempotency-Key</c> parameter, and that header
/// is the entire exactly-once story (wallet-service dedupes on key + request fingerprint).
/// </summary>
public interface IWalletCommissionDebitClient
{
    /// <summary>The jeeber's fee wallet: active, in the configured currency, and NOT a COD leg.
    /// Returns null when no such wallet exists — never falls back to a COD wallet.</summary>
    Task<Guid?> ResolveFeeWalletAsync(Guid holderId, CancellationToken ct);

    /// <summary>The platform counterparty (wallet-service <c>__SYSTEM__</c> holder, Guid.Empty).</summary>
    Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct);

    /// <summary>POST Transaction/initiate. Writes a Pending header; moves no money.</summary>
    Task<Guid> InitiateAsync(
        Guid sourceWalletId, Guid destinationWalletId, decimal amount,
        string tag, string notes, string idempotencyKey, CancellationToken ct);

    /// <summary>POST Transaction/{id}/execute. Idempotent upstream on the transaction id.</summary>
    Task ExecuteAsync(Guid transactionId, CancellationToken ct);

    /// <summary>POST Transaction/{id}/abort. Only ever called on a deterministic rejection.</summary>
    Task AbortAsync(Guid transactionId, CancellationToken ct);
}

public sealed class WalletCommissionDebitClient : IWalletCommissionDebitClient
{
    public const string HttpClientName = "wallet-commission-api";
    public const string IdempotencyHeader = "Idempotency-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly int _currencyId;

    public WalletCommissionDebitClient(HttpClient http, int currencyId)
    {
        _http = http;
        _currencyId = currencyId;
    }

    public async Task<Guid?> ResolveFeeWalletAsync(Guid holderId, CancellationToken ct)
    {
        var holder = await GetAsync<HolderWalletsWire>($"Wallet/holder/{holderId:D}/wallets", ct);
        return PickWallet(holder?.Wallets, requireSpendable: true);
    }

    public async Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct)
    {
        var system = await GetAsync<HolderWalletsWire>("system-wallet", ct);
        return PickWallet(system?.Wallets, requireSpendable: false);
    }

    public async Task<Guid> InitiateAsync(
        Guid sourceWalletId, Guid destinationWalletId, decimal amount,
        string tag, string notes, string idempotencyKey, CancellationToken ct)
    {
        var body = new InitiateWire(
            ServiceName: "jeeb-gateway",
            Tag: tag,
            Notes: notes,
            // The caller supplies the complete accounting entry; wallet-service must not append
            // its own configured fee leg on top of a fee.
            ApplyConfiguredFees: false,
            Transactions: [new LegWire(sourceWalletId, destinationWalletId, amount, IsAdditionalFees: true)]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "Transaction/initiate")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);

        using var response = await SendAsync(request, "initiate the commission debit", ct);
        await EnsureSuccessAsync(response, "initiate the commission debit", ct);

        var txn = await ReadAsync<TransactionWire>(response, "initiate the commission debit", ct);
        var txId = txn?.TransactionHeader?.TxId ?? Guid.Empty;
        if (txId == Guid.Empty)
        {
            throw new WalletCommissionDebitException(
                "wallet-service returned no transaction id to execute.", response.StatusCode);
        }

        return txId;
    }

    public async Task ExecuteAsync(Guid transactionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"Transaction/{transactionId:D}/execute");
        using var response = await SendAsync(request, "execute the commission debit", ct);
        await EnsureSuccessAsync(response, "execute the commission debit", ct);
    }

    public async Task AbortAsync(Guid transactionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"Transaction/{transactionId:D}/abort");
        using var response = await SendAsync(request, "abort the commission debit", ct);
        await EnsureSuccessAsync(response, "abort the commission debit", ct);
    }

    /// <summary>Currency-pinned + active, and (for a holder) never a COD leg — the ratified
    /// <see cref="SpendableWalletTypes"/> predicate, so the fee wallet and the offer-time guard
    /// can never disagree about which balance is spendable.</summary>
    private Guid? PickWallet(IReadOnlyList<WalletWire>? wallets, bool requireSpendable)
    {
        if (wallets is null) return null;

        foreach (var wallet in wallets)
        {
            if (wallet is null || !wallet.IsActive) continue;
            if (wallet.CurrencyId != _currencyId) continue;
            if (requireSpendable && !SpendableWalletTypes.IsSpendable(wallet.Type)) continue;
            if (wallet.WalletId == Guid.Empty) continue;
            return wallet.WalletId;
        }

        return null;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, $"read {url}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        await EnsureSuccessAsync(response, $"read {url}", ct);
        return await ReadAsync<T>(response, $"read {url}", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No status code => ambiguous. The collector must never abort on this.
            throw new WalletCommissionDebitException(
                $"wallet-service transport fault while trying to {operation}.", null, ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new WalletCommissionDebitException(
            $"wallet-service could not {operation} (HTTP {(int)response.StatusCode}): {Truncate(body)}",
            response.StatusCode);
    }

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new WalletCommissionDebitException(
                $"wallet-service returned an unreadable body while trying to {operation}.",
                response.StatusCode, ex);
        }
    }

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300];

    // ── wire shapes (wallet-service DTOs; opaque strings, no jeeb vocabulary leaves the gateway) ──

    private sealed record InitiateWire(
        string ServiceName,
        string Tag,
        string Notes,
        bool ApplyConfiguredFees,
        IReadOnlyList<LegWire> Transactions);

    private sealed record LegWire(
        Guid SourceWalletId,
        Guid DestinationWalletId,
        decimal Amount,
        bool IsAdditionalFees);

    private sealed class TransactionWire
    {
        public TransactionHeaderWire? TransactionHeader { get; set; }
    }

    private sealed class TransactionHeaderWire
    {
        public Guid TxId { get; set; }
    }

    private sealed class HolderWalletsWire
    {
        public IReadOnlyList<WalletWire>? Wallets { get; set; }
    }

    private sealed class WalletWire
    {
        public Guid WalletId { get; set; }

        [JsonPropertyName("currencyID")]
        public int CurrencyId { get; set; }

        public string? Type { get; set; }
        public bool IsActive { get; set; }
    }
}
