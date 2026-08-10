using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Financials;

public sealed class WalletSettlementUnavailableException : Exception
{
    public WalletSettlementUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

/// <summary>
/// Wallet-authoritative COD settlement writer. Product accounting is sent as
/// explicit generic transaction legs; wallet-service owns idempotency, the
/// transaction header, execution, and every balance mutation.
/// </summary>
public sealed class WalletSettlementLedgerClient : ISettlementLedgerClient
{
    public const string HttpClientName = "wallet-settlement-api";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<WalletSettlementLedgerClient> _log;

    public WalletSettlementLedgerClient(
        IHttpClientFactory clients,
        ILogger<WalletSettlementLedgerClient> log)
    {
        _clients = clients;
        _log = log;
    }

    public async Task<LedgerEntryResponse> PostLedgerEntryAsync(
        LedgerEntryRequest request,
        CancellationToken ct)
    {
        Validate(request);
        var holderId = Guid.Parse(request.JeeberId);
        var client = _clients.CreateClient(HttpClientName);

        try
        {
            var currencyId = await ResolveCurrencyIdAsync(client, request.Currency, ct);
            var holderWalletTask = ReadWalletAsync(
                client, $"Wallet/holder/{holderId:D}/wallets", "Jeeber", request.Currency, currencyId, ct);
            var systemWalletTask = ReadWalletAsync(
                client, "system-wallet", "system", request.Currency, currencyId, ct);
            await Task.WhenAll(holderWalletTask, systemWalletTask);

            var holderWallet = await holderWalletTask;
            var systemWallet = await systemWalletTask;
            var legs = new List<WalletTransactionLeg>
            {
                new(systemWallet, holderWallet, request.GoodsCost, IsAdditionalFees: false),
            };
            if (request.Commission > 0m)
            {
                legs.Add(new WalletTransactionLeg(
                    holderWallet, systemWallet, request.Commission, IsAdditionalFees: true));
            }
            if (request.Insurance > 0m)
            {
                legs.Add(new WalletTransactionLeg(
                    holderWallet, systemWallet, request.Insurance, IsAdditionalFees: true));
            }

            using var initiate = new HttpRequestMessage(HttpMethod.Post, "Transaction/initiate")
            {
                Content = JsonContent.Create(new WalletTransactionRequest(
                    ServiceName: "jeeb-gateway",
                    Tag: "cod-settlement",
                    Notes: BuildNotes(request),
                    ExternalReference: request.DeliveryId,
                    ApplyConfiguredFees: false,
                    Transactions: legs), options: Json),
            };
            initiate.Headers.TryAddWithoutValidation(
                "Idempotency-Key", WalletIdempotencyKey(request.IdempotencyKey));

            using var initiatedResponse = await client.SendAsync(
                initiate, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(initiatedResponse, "initiate", request.IdempotencyKey, ct);
            await using var initiatedBody = await initiatedResponse.Content.ReadAsStreamAsync(ct);
            var initiated = await JsonSerializer.DeserializeAsync<WalletTransactionResponse>(
                initiatedBody, Json, ct);
            if (initiated?.TransactionHeader is null || initiated.TransactionHeader.TxId == Guid.Empty)
            {
                throw new WalletSettlementUnavailableException(
                    "Wallet-service returned an invalid settlement transaction response.");
            }

            using var executedResponse = await client.PostAsync(
                $"Transaction/{initiated.TransactionHeader.TxId:D}/execute", content: null, ct);
            await EnsureSuccessAsync(executedResponse, "execute", request.IdempotencyKey, ct);

            _log.LogInformation(
                "Wallet settlement posted settlementId={SettlementId} deliveryId={DeliveryId} transactionHeaderId={TransactionHeaderId}",
                request.IdempotencyKey, request.DeliveryId, initiated.TransactionHeader.TxId);
            return new LedgerEntryResponse
            {
                LedgerEntryId = initiated.TransactionHeader.TxId.ToString("D"),
                PostedAt = initiated.TransactionHeader.CreatedAt.ToUniversalTime(),
            };
        }
        catch (WalletSettlementUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new WalletSettlementUnavailableException(
                "Wallet-service settlement request timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new WalletSettlementUnavailableException(
                "Wallet-service settlement request failed.", ex);
        }
    }

    private static async Task<int> ResolveCurrencyIdAsync(
        HttpClient client, string currencyCode, CancellationToken ct)
    {
        using var response = await client.GetAsync(
            "Fees/currencies", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "read currencies", settlementId: null, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var currencies = await JsonSerializer.DeserializeAsync<List<WalletCurrency>>(body, Json, ct)
            ?? throw new WalletSettlementUnavailableException(
                "Wallet-service returned an invalid currency response.");
        var matches = currencies.Where(currency =>
            string.Equals(currency.Code, currencyCode, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Id <= 0)
        {
            throw new WalletSettlementUnavailableException(
                $"Wallet-service must expose exactly one configured '{currencyCode}' currency.");
        }
        return matches[0].Id;
    }

    private static async Task<Guid> ReadWalletAsync(
        HttpClient client,
        string path,
        string ownerLabel,
        string currencyCode,
        int currencyId,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, $"read {ownerLabel} wallets", settlementId: null, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<WalletHolderResponse>(body, Json, ct)
            ?? throw new WalletSettlementUnavailableException(
                $"Wallet-service returned an invalid {ownerLabel} wallet response.");
        var matches = (payload.Wallets ?? Array.Empty<WalletAccount>())
            .Where(wallet => wallet.IsActive && wallet.CurrencyId == currencyId)
            .ToArray();
        if (matches.Length != 1 || matches[0].WalletId == Guid.Empty)
        {
            throw new WalletSettlementUnavailableException(
                $"Wallet-service must expose exactly one active {ownerLabel} '{currencyCode}' wallet.");
        }
        return matches[0].WalletId;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        string? settlementId,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        _ = await response.Content.ReadAsByteArrayAsync(ct);
        throw new WalletSettlementUnavailableException(
            $"Wallet-service could not {operation} settlement '{settlementId ?? "(pending)"}' " +
            $"(status {(int)response.StatusCode}).");
    }

    private static void Validate(LedgerEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntryType);
        if (!Guid.TryParse(request.JeeberId, out var holderId) || holderId == Guid.Empty)
            throw new ArgumentException("JeeberId must be a non-system wallet holder UUID", nameof(request));
        if (request.GoodsCost <= 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "GoodsCost must be positive");
        if (request.Commission < 0m || request.Insurance < 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "Fees cannot be negative");
        if (request.Commission + request.Insurance > request.GoodsCost)
            throw new ArgumentOutOfRangeException(nameof(request), "Fees cannot exceed the gross settlement");
        if (request.Total != request.Commission + request.Insurance)
            throw new ArgumentException("Total must equal commission plus insurance", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Currency);
        if (!string.Equals(request.PaymentMethod, "cash", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only cash settlements are supported", nameof(request));
    }

    private static string WalletIdempotencyKey(string settlementId)
    {
        var value = $"settlement:{settlementId.Trim()}";
        if (value.Length > 255)
            throw new ArgumentException("Settlement idempotency key is too long", nameof(settlementId));
        return value;
    }

    private static string BuildNotes(LedgerEntryRequest request) => string.Join(';',
        "schema=jeeb-cod-v1",
        $"settlement={request.IdempotencyKey.Trim()}",
        $"delivery={request.DeliveryId.Trim()}",
        $"client={request.ClientId.Trim()}",
        $"gross={request.GoodsCost.ToString(CultureInfo.InvariantCulture)}",
        $"commission={request.Commission.ToString(CultureInfo.InvariantCulture)}",
        $"insurance={request.Insurance.ToString(CultureInfo.InvariantCulture)}",
        $"currency={request.Currency.Trim().ToUpperInvariant()}");

    private sealed class WalletCurrency
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        public string? Code { get; set; }
    }

    private sealed class WalletHolderResponse
    {
        public IReadOnlyList<WalletAccount>? Wallets { get; set; }
    }

    private sealed class WalletAccount
    {
        public Guid WalletId { get; set; }
        [JsonPropertyName("currencyID")]
        public int CurrencyId { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed record WalletTransactionRequest(
        string ServiceName,
        string Tag,
        string Notes,
        string ExternalReference,
        bool ApplyConfiguredFees,
        IReadOnlyList<WalletTransactionLeg> Transactions);

    private sealed record WalletTransactionLeg(
        Guid SourceWalletId,
        Guid DestinationWalletId,
        decimal Amount,
        bool IsAdditionalFees);

    private sealed class WalletTransactionResponse
    {
        public WalletTransactionHeader? TransactionHeader { get; set; }
    }

    private sealed class WalletTransactionHeader
    {
        public Guid TxId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
