using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Financials;

/// <summary>
/// Raised when wallet-service cannot durably accept or execute a settlement transaction.
/// Callers must leave the settlement outbox row unstamped so the reconciler can replay it.
/// </summary>
public sealed class WalletSettlementUnavailableException : Exception
{
    public WalletSettlementUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Wallet-authoritative COD settlement writer. The gateway supplies product accounting as
/// explicit generic transaction legs; wallet-service owns the transaction header, details,
/// idempotency key, execution status, and every resulting balance mutation.
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
            var holderWalletTask = ReadWalletsAsync(
                client, $"Wallet/holder/{holderId:D}/wallets", "Jeeber", request.Currency, currencyId, ct);
            var systemWalletTask = ReadWalletsAsync(
                client, "system-wallet", "system", request.Currency, currencyId, ct);
            await Task.WhenAll(holderWalletTask, systemWalletTask);

            var holderWallet = holderWalletTask.Result;
            var systemWallet = systemWalletTask.Result;
            var legs = new List<WalletTransactionLeg>
            {
                // Gross COD value is issued from the generic system liability account.
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

            var walletIdempotencyKey = WalletIdempotencyKey(request.IdempotencyKey);
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
            initiate.Headers.TryAddWithoutValidation("Idempotency-Key", walletIdempotencyKey);

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

            // Execution is idempotent on the transaction-header id. A lost response is safely
            // retried by the durable settlement reconciler using the same initiation key.
            using var executedResponse = await client.PostAsync(
                $"Transaction/{initiated.TransactionHeader.TxId:D}/execute", content: null, ct);
            await EnsureSuccessAsync(executedResponse, "execute", request.IdempotencyKey, ct);

            var postedAt = initiated.TransactionHeader.CreatedAt.ToUniversalTime();
            _log.LogInformation(
                "Wallet settlement posted settlementId={SettlementId} deliveryId={DeliveryId} transactionHeaderId={TransactionHeaderId}",
                request.IdempotencyKey, request.DeliveryId, initiated.TransactionHeader.TxId);
            return new LedgerEntryResponse
            {
                LedgerEntryId = initiated.TransactionHeader.TxId.ToString("D"),
                PostedAt = postedAt,
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
        HttpClient client,
        string currencyCode,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(
            "Fees/currencies", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "read currencies", settlementId: null, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var currencies = await JsonSerializer.DeserializeAsync<List<WalletCurrency>>(body, Json, ct)
            ?? throw new WalletSettlementUnavailableException(
                "Wallet-service returned an invalid currency response.");
        var matches = currencies
            .Where(currency => string.Equals(
                currency.Code, currencyCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || matches[0].Id <= 0)
        {
            throw new WalletSettlementUnavailableException(
                $"Wallet-service must expose exactly one configured '{currencyCode}' currency.");
        }
        return matches[0].Id;
    }

    private static async Task<Guid> ReadWalletsAsync(
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

        // Never surface the upstream response body: wallet errors can contain implementation
        // details. The status and operation are sufficient for structured diagnostics.
        _ = await response.Content.ReadAsByteArrayAsync(ct);
        throw new WalletSettlementUnavailableException(
            $"Wallet-service could not {operation} settlement '{settlementId ?? "(pending)"}' " +
            $"(status {(int)response.StatusCode}).");
    }

    private static void Validate(LedgerEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DeliveryId))
            throw new ArgumentException("DeliveryId required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ClientId))
            throw new ArgumentException("ClientId required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EntryType))
            throw new ArgumentException("EntryType required", nameof(request));
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
        if (string.IsNullOrWhiteSpace(request.Currency))
            throw new ArgumentException("Currency required", nameof(request));
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

/// <summary>Read-only legacy settlement ledger projection used during cutover comparison.</summary>
public interface ISettlementLedgerShadowReader
{
    Task<LegacySettlementLedgerEntry?> ReadAsync(string idempotencyKey, CancellationToken ct);
}

public sealed record LegacySettlementLedgerEntry(
    string IdempotencyKey,
    string DeliveryId,
    string JeeberId,
    string ClientId,
    string EntryType,
    decimal GoodsCost,
    decimal Commission,
    decimal Insurance,
    decimal Total,
    string Currency,
    string PaymentMethod);

/// <summary>
/// Temporary, read-only gateway-Postgres shadow. It deliberately has no insert/update method.
/// </summary>
public sealed class PostgresSettlementLedgerShadowReader : ISettlementLedgerShadowReader
{
    private readonly INpgsqlConnectionFactory _db;

    public PostgresSettlementLedgerShadowReader(INpgsqlConnectionFactory db) => _db = db;

    public async Task<LegacySettlementLedgerEntry?> ReadAsync(
        string idempotencyKey,
        CancellationToken ct)
    {
        const string sql = """
            SELECT idempotency_key, delivery_id, jeeber_id, client_id, entry_type,
                   goods_cost, commission, insurance, total, currency, payment_method
            FROM settlement_ledger_entries
            WHERE idempotency_key = @IdempotencyKey
            """;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("IdempotencyKey", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new LegacySettlementLedgerEntry(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7),
            reader.GetDecimal(8), reader.GetString(9), reader.GetString(10));
    }
}

/// <summary>
/// Returns only the wallet-service result, then compares the immutable request with the legacy
/// gateway ledger if present. Shadow absence/failure/mismatch is observable and never changes the
/// settlement response.
/// </summary>
public sealed class ShadowComparingSettlementLedgerClient : ISettlementLedgerClient
{
    private readonly WalletSettlementLedgerClient _primary;
    private readonly ISettlementLedgerShadowReader _shadow;
    private readonly ILogger<ShadowComparingSettlementLedgerClient> _log;

    public ShadowComparingSettlementLedgerClient(
        WalletSettlementLedgerClient primary,
        ISettlementLedgerShadowReader shadow,
        ILogger<ShadowComparingSettlementLedgerClient> log)
    {
        _primary = primary;
        _shadow = shadow;
        _log = log;
    }

    public async Task<LedgerEntryResponse> PostLedgerEntryAsync(
        LedgerEntryRequest request,
        CancellationToken ct)
    {
        var result = await _primary.PostLedgerEntryAsync(request, ct);
        try
        {
            var legacy = await _shadow.ReadAsync(request.IdempotencyKey, ct);
            var primaryDigest = Digest(request);
            if (legacy is null)
            {
                _log.LogWarning(
                    "SettlementLedgerShadowMissing settlementId={SettlementId} deliveryId={DeliveryId} primaryDigest={PrimaryDigest}",
                    request.IdempotencyKey, request.DeliveryId, primaryDigest);
            }
            else
            {
                var shadowDigest = Digest(legacy);
                if (string.Equals(primaryDigest, shadowDigest, StringComparison.Ordinal))
                {
                    _log.LogInformation(
                        "SettlementLedgerShadowMatch settlementId={SettlementId} deliveryId={DeliveryId} digest={Digest}",
                        request.IdempotencyKey, request.DeliveryId, primaryDigest);
                }
                else
                {
                    _log.LogWarning(
                        "SettlementLedgerShadowMismatch settlementId={SettlementId} deliveryId={DeliveryId} primaryDigest={PrimaryDigest} shadowDigest={ShadowDigest}",
                        request.IdempotencyKey, request.DeliveryId, primaryDigest, shadowDigest);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Settlement ledger shadow read failed for settlement {SettlementId}; wallet result remains authoritative.",
                request.IdempotencyKey);
        }
        return result;
    }

    private static string Digest(LedgerEntryRequest value) => DigestCanonical(string.Join('|',
        value.IdempotencyKey, value.DeliveryId, value.JeeberId, value.ClientId, value.EntryType,
        value.GoodsCost.ToString(CultureInfo.InvariantCulture),
        value.Commission.ToString(CultureInfo.InvariantCulture),
        value.Insurance.ToString(CultureInfo.InvariantCulture),
        value.Total.ToString(CultureInfo.InvariantCulture),
        value.Currency.ToUpperInvariant(), value.PaymentMethod.ToLowerInvariant()));

    private static string Digest(LegacySettlementLedgerEntry value) => DigestCanonical(string.Join('|',
        value.IdempotencyKey, value.DeliveryId, value.JeeberId, value.ClientId, value.EntryType,
        value.GoodsCost.ToString(CultureInfo.InvariantCulture),
        value.Commission.ToString(CultureInfo.InvariantCulture),
        value.Insurance.ToString(CultureInfo.InvariantCulture),
        value.Total.ToString(CultureInfo.InvariantCulture),
        value.Currency.ToUpperInvariant(), value.PaymentMethod.ToLowerInvariant()));

    private static string DigestCanonical(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
