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
    /// <summary>wallet-service's own ProblemDetails type for a refused debit.</summary>
    public const string InsufficientBalanceType = "https://wallet.olivium.dev/errors/insufficient-balance";

    /// <summary>wallet-service's own ProblemDetails type for the same key with a different body.</summary>
    public const string IdempotencyConflictType = "https://wallet.olivium.dev/errors/idempotency-conflict";

    public WalletCommissionDebitException(
        string message, HttpStatusCode? statusCode, string? problemType = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ProblemType = problemType;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>The upstream ProblemDetails `type`, when the body carried one.</summary>
    public string? ProblemType { get; }

    /// <summary>A deterministic upstream rejection: the money did NOT move on this call.</summary>
    public bool IsDeterministicRejection =>
        StatusCode is { } s && (int)s >= 400 && (int)s < 500;

    /// <summary>Read off wallet-service's own problem type, not guessed from the status code —
    /// 409 is also how an idempotency conflict and other refusals surface.</summary>
    public bool IsInsufficientBalance =>
        string.Equals(ProblemType, InsufficientBalanceType, StringComparison.Ordinal);

    /// <summary>Same key, different body: a real accounting divergence, never a retryable blip.</summary>
    public bool IsIdempotencyConflict =>
        string.Equals(ProblemType, IdempotencyConflictType, StringComparison.Ordinal);
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
        string tag, string notes, string idempotencyKey, string externalReference, CancellationToken ct);

    /// <summary>Pure reads. Returns exactly one EXECUTED, identity/amount/currency-bound commission
    /// debit; pending, aborted, incomplete and ambiguous matches never prove collection.</summary>
    Task<Guid?> FindExecutedDebitAsync(CommissionDebitLookup expected, CancellationToken ct);

    /// <summary>POST Transaction/{id}/execute. Idempotent upstream on the transaction id.</summary>
    Task ExecuteAsync(Guid transactionId, CancellationToken ct);

    /// <summary>POST Transaction/{id}/abort. Only ever called on a deterministic rejection.</summary>
    Task AbortAsync(Guid transactionId, CancellationToken ct);
}

public sealed record CommissionDebitLookup(
    string ExternalReference, string IdempotencyKey, Guid HolderId, decimal Amount, string Tag,
    string Currency);

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
        if (holderId == Guid.Empty || !await HasCurrencyMappingAsync(SettlementService.CurrencyUsd, ct)) return null;
        var holder = await GetAsync<HolderWalletsWire>($"Wallet/holder/{holderId:D}/wallets", ct);
        if (holder?.WalletHolder?.HolderId != holderId) return null;
        return PickWallet(holder.Wallets, holderId, requireSpendable: true);
    }

    public async Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct)
    {
        if (!await HasCurrencyMappingAsync(SettlementService.CurrencyUsd, ct)) return null;
        var system = await GetAsync<HolderWalletsWire>("system-wallet", ct);
        if (!IsSystemHolder(system?.WalletHolder)) return null;
        return PickWallet(system?.Wallets, Guid.Empty, requireSpendable: false);
    }

    public async Task<Guid> InitiateAsync(
        Guid sourceWalletId, Guid destinationWalletId, decimal amount,
        string tag, string notes, string idempotencyKey, string externalReference, CancellationToken ct)
    {
        var body = new InitiateWire(
            ServiceName: "jeeb-gateway",
            Tag: tag,
            Notes: notes,
            ExternalReference: externalReference,
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

    public async Task<Guid?> FindExecutedDebitAsync(CommissionDebitLookup expected, CancellationToken ct)
    {
        if (expected.HolderId == Guid.Empty || expected.Amount <= 0m || _currencyId <= 0
            || string.IsNullOrWhiteSpace(expected.ExternalReference)
            || string.IsNullOrWhiteSpace(expected.IdempotencyKey)
            || string.IsNullOrWhiteSpace(expected.Tag)
            // The gateway's settlement contract is USD-only. Do not relabel an unsupported
            // historical settlement or infer its currency from a deployment-specific numeric id.
            || !string.Equals(expected.Currency, SettlementService.CurrencyUsd, StringComparison.Ordinal)) return null;

        var found = await GetAsync<List<TransactionWire>>(
            $"Transaction/by-external-reference/{Uri.EscapeDataString(expected.ExternalReference)}", ct);
        var candidates = found?.Where(transaction =>
            transaction.TransactionHeader is { Status: 0 } header
            && header.TxId != Guid.Empty
            && string.Equals(header.ServiceName, "jeeb-gateway", StringComparison.Ordinal)
            && string.Equals(header.Tag, expected.Tag, StringComparison.Ordinal)
            && string.Equals(header.ExternalReference, expected.ExternalReference, StringComparison.Ordinal)
            && string.Equals(header.IdempotencyKey, expected.IdempotencyKey, StringComparison.Ordinal)
            && transaction.TransactionDetails is { Count: 1 } details
            && details[0].TxHeaderId == header.TxId
            && details[0].Amount == expected.Amount
            && details[0].IsAdditionalFees == true).ToArray();
        if (candidates is not { Length: > 0 }) return null;

        if (!await HasCurrencyMappingAsync(expected.Currency, ct)) return null;

        var holder = await GetAsync<HolderWalletsWire>($"Wallet/holder/{expected.HolderId:D}/wallets", ct);
        var system = await GetAsync<HolderWalletsWire>("system-wallet", ct);
        // Link historical executed entries even if a wallet was subsequently deactivated.
        // Ownership and currency, not current spendability, establish the accounting identity.
        var sourceIds = holder?.Wallets?.Where(wallet => wallet.HolderId == expected.HolderId
                && wallet.CurrencyId == _currencyId && SpendableWalletTypes.IsSpendable(wallet.Type)
                && wallet.WalletId != Guid.Empty)
            .Select(wallet => wallet.WalletId).ToHashSet() ?? new HashSet<Guid>();
        if (!IsSystemHolder(system?.WalletHolder)) return null;
        var destinationIds = system?.Wallets?.Where(wallet => wallet.HolderId == Guid.Empty
                && wallet.CurrencyId == _currencyId && wallet.WalletId != Guid.Empty)
            .Select(wallet => wallet.WalletId).ToHashSet() ?? new HashSet<Guid>();

        var matches = candidates.Where(transaction =>
                sourceIds.Contains(transaction.TransactionDetails![0].SourceWalletId)
                && destinationIds.Contains(transaction.TransactionDetails[0].DestinationWalletId)
                && transaction.TransactionDetails[0].SourceWalletId != transaction.TransactionDetails[0].DestinationWalletId)
            .Select(transaction => transaction.TransactionHeader!.TxId).Distinct().Take(2).ToArray();
        // An opaque reference is not unique. Refuse ambiguity instead of stamping the newest row.
        return matches.Length == 1 ? matches[0] : null;
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

    private async Task<bool> HasCurrencyMappingAsync(string expectedCurrency, CancellationToken ct)
    {
        if (_currencyId <= 0
            || !string.Equals(expectedCurrency, SettlementService.CurrencyUsd, StringComparison.Ordinal)) return false;

        var currencies = await GetAsync<List<CurrencyWire?>>("Fees/currencies", ct);
        var configuredCurrencies = currencies?.Where(currency => currency?.Id == _currencyId).ToArray();
        // Both directions must be unique: duplicate ids, duplicate codes and missing metadata
        // cannot prove which money moved. Numeric ids belong to wallet-service, not the gateway.
        return configuredCurrencies is { Length: 1 }
            && string.Equals(configuredCurrencies[0]?.Code, expectedCurrency, StringComparison.OrdinalIgnoreCase)
            && currencies!.Count(currency => string.Equals(currency?.Code, expectedCurrency, StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private static bool IsSystemHolder(HolderWire? holder) =>
        holder?.HolderId == Guid.Empty
        && string.Equals(holder.HolderName, "__SYSTEM__", StringComparison.Ordinal)
        && string.Equals(holder.HolderType, "__SYSTEM__", StringComparison.Ordinal);

    /// <summary>A new debit requires one unambiguous, active, owned wallet in the verified
    /// currency. Historical execution verification deliberately does not require active wallets.</summary>
    private Guid? PickWallet(IReadOnlyList<WalletWire>? wallets, Guid holderId, bool requireSpendable)
    {
        var matches = wallets?.Where(wallet => wallet is { IsActive: true }
                && wallet.HolderId == holderId && wallet.CurrencyId == _currencyId
                && (!requireSpendable || SpendableWalletTypes.IsSpendable(wallet.Type))
                && wallet.WalletId != Guid.Empty)
            .Select(wallet => wallet.WalletId).Take(2).ToArray();
        return matches is { Length: 1 } ? matches[0] : null;
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
                $"wallet-service transport fault while trying to {operation}.", null, null, ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new WalletCommissionDebitException(
            $"wallet-service could not {operation} (HTTP {(int)response.StatusCode}): {Truncate(body)}",
            response.StatusCode, ReadProblemType(body));
    }

    /// <summary>wallet-service answers refusals with ProblemDetails; the `type` is the only
    /// non-guessing way to tell insufficient balance from an idempotency conflict (both are 409).</summary>
    private static string? ReadProblemType(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
                response.StatusCode, null, ex);
        }
    }

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300];

    // ── wire shapes (wallet-service DTOs; opaque strings, no jeeb vocabulary leaves the gateway) ──

    private sealed record InitiateWire(
        string ServiceName,
        string Tag,
        string Notes,
        string ExternalReference,
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
        public IReadOnlyList<TransactionDetailWire>? TransactionDetails { get; set; }
    }

    private sealed class TransactionHeaderWire
    {
        public Guid TxId { get; set; }
        public int? Status { get; set; }
        public string? ServiceName { get; set; }
        public string? Tag { get; set; }
        public string? ExternalReference { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    private sealed class TransactionDetailWire
    {
        public Guid TxHeaderId { get; set; }
        public Guid SourceWalletId { get; set; }
        public Guid DestinationWalletId { get; set; }
        public decimal? Amount { get; set; }
        public bool? IsAdditionalFees { get; set; }
    }

    private sealed class HolderWalletsWire
    {
        public HolderWire? WalletHolder { get; set; }
        public IReadOnlyList<WalletWire>? Wallets { get; set; }
    }

    private sealed class HolderWire
    {
        public Guid? HolderId { get; set; }
        public string? HolderName { get; set; }
        public string? HolderType { get; set; }
    }

    private sealed class CurrencyWire
    {
        public int? Id { get; set; }
        public string? Code { get; set; }
    }

    private sealed class WalletWire
    {
        public Guid WalletId { get; set; }
        public Guid? HolderId { get; set; }

        [JsonPropertyName("currencyID")]
        public int CurrencyId { get; set; }

        public string? Type { get; set; }
        public bool IsActive { get; set; }
    }
}
