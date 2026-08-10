using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.JeebWallet;

/// <summary>
/// Fail-safe wallet provisioning boundary for a user that is about to receive the opaque
/// <c>driver</c> role. Wallet-service stays role-agnostic: it receives only an opaque holder id,
/// holder metadata, and generic currency wallets.
/// </summary>
public interface IJeeberWalletProvisioner
{
    Task EnsureAsync(Guid holderId, CancellationToken ct);
}

public sealed class WalletProvisioningUnavailableException : Exception
{
    public WalletProvisioningUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class WalletServiceJeeberWalletProvisioner : IJeeberWalletProvisioner
{
    public const string HttpClientName = "wallet-holder-provisioning-api";

    private const string DefaultHolderType = "jeeber";
    private const string DefaultWalletType = "jeeb";
    private const string ProvisioningNote = "gateway-jeeber-role-activation";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;

    public WalletServiceJeeberWalletProvisioner(IHttpClientFactory clients) => _clients = clients;

    public async Task EnsureAsync(Guid holderId, CancellationToken ct)
    {
        if (holderId == Guid.Empty)
            throw new ArgumentException("The system holder cannot receive a Jeeber wallet.", nameof(holderId));

        var client = _clients.CreateClient(HttpClientName);
        try
        {
            var currencies = await ReadCurrenciesAsync(client, ct);
            var current = await ReadHolderAsync(client, holderId, ct);
            var request = BuildEnsureRequest(holderId, currencies, current);

            using var response = await client.PutAsJsonAsync("Wallet/holder/ensure", request, Json, ct);
            await EnsureSuccessAsync(response, "ensure holder wallets", ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var ensured = await JsonSerializer.DeserializeAsync<WalletHolderResponse>(body, Json, ct)
                ?? throw new WalletProvisioningUnavailableException(
                    "Wallet-service returned an invalid holder provisioning response.");

            VerifyReady(holderId, currencies, ensured);
        }
        catch (WalletProvisioningUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service holder provisioning timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service holder provisioning failed.", ex);
        }
    }

    private static async Task<IReadOnlyList<WalletCurrency>> ReadCurrenciesAsync(
        HttpClient client,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(
            "Fees/currencies", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "read configured currencies", ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var currencies = await JsonSerializer.DeserializeAsync<List<WalletCurrency>>(body, Json, ct)
            ?? throw new WalletProvisioningUnavailableException(
                "Wallet-service returned an invalid currency response.");
        if (currencies.Count == 0
            || currencies.Any(currency => currency.Id <= 0)
            || currencies.Any(currency => string.IsNullOrWhiteSpace(currency.Code))
            || currencies.GroupBy(currency => currency.Id).Any(group => group.Count() != 1)
            || currencies.GroupBy(currency => currency.Code, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1))
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service must expose a non-empty, unambiguous configured currency set.");
        }

        return currencies;
    }

    private static async Task<WalletHolderResponse> ReadHolderAsync(
        HttpClient client,
        Guid holderId,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(
            $"Wallet/holder/{holderId:D}/wallets", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "read holder wallets", ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<WalletHolderResponse>(body, Json, ct)
            ?? throw new WalletProvisioningUnavailableException(
                "Wallet-service returned an invalid holder response.");
    }

    private static EnsureHolderRequest BuildEnsureRequest(
        Guid holderId,
        IReadOnlyList<WalletCurrency> currencies,
        WalletHolderResponse current)
    {
        var active = (current.Wallets ?? Array.Empty<WalletAccount>())
            .Where(wallet => wallet.IsActive)
            .ToArray();
        var requestedWallets = new List<EnsureWallet>(currencies.Count);
        foreach (var currency in currencies)
        {
            var matches = active.Where(wallet => wallet.CurrencyId == currency.Id).ToArray();
            if (matches.Length > 1)
            {
                throw new WalletProvisioningUnavailableException(
                    $"Holder has multiple active wallets for configured currency id {currency.Id}.");
            }
            if (matches.Length == 1 && matches[0].WalletId == Guid.Empty)
            {
                throw new WalletProvisioningUnavailableException(
                    $"Holder wallet for configured currency id {currency.Id} has no durable id.");
            }

            var walletType = matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].Type)
                ? matches[0].Type
                : DefaultWalletType;
            requestedWallets.Add(new EnsureWallet(currency.Id, walletType, ProvisioningNote));
        }

        var holder = current.WalletHolder;
        if (holder is not null && holder.HolderId != holderId)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service returned a holder id that does not match the requested user.");
        }

        return new EnsureHolderRequest(
            new WalletHolderPayload(
                holderId,
                string.IsNullOrWhiteSpace(holder?.HolderName) ? holderId.ToString("D") : holder.HolderName,
                string.IsNullOrWhiteSpace(holder?.HolderType) ? DefaultHolderType : holder.HolderType),
            requestedWallets);
    }

    private static void VerifyReady(
        Guid holderId,
        IReadOnlyList<WalletCurrency> currencies,
        WalletHolderResponse response)
    {
        if (response.WalletHolder is null
            || response.WalletHolder.HolderId != holderId
            || !response.WalletHolder.IsActive)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service did not return the expected active holder.");
        }

        var active = (response.Wallets ?? Array.Empty<WalletAccount>())
            .Where(wallet => wallet.IsActive)
            .ToArray();
        if (currencies.Any(currency =>
            {
                var matches = active.Where(wallet => wallet.CurrencyId == currency.Id).ToArray();
                return matches.Length != 1 || matches[0].WalletId == Guid.Empty;
            }))
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service did not converge to exactly one active wallet per configured currency.");
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        _ = await response.Content.ReadAsByteArrayAsync(ct);
        throw new WalletProvisioningUnavailableException(
            $"Wallet-service could not {operation} (status {(int)response.StatusCode}).");
    }

    private sealed class WalletCurrency
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }

    private sealed class WalletHolderResponse
    {
        public WalletHolder? WalletHolder { get; set; }
        public IReadOnlyList<WalletAccount>? Wallets { get; set; }
    }

    private sealed class WalletHolder
    {
        public Guid HolderId { get; set; }
        public string HolderName { get; set; } = string.Empty;
        public string HolderType { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    private sealed class WalletAccount
    {
        public Guid WalletId { get; set; }

        [JsonPropertyName("currencyID")]
        public int CurrencyId { get; set; }

        public string Type { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    private sealed record EnsureHolderRequest(
        WalletHolderPayload WalletHolder,
        IReadOnlyList<EnsureWallet> Wallets);

    private sealed record WalletHolderPayload(
        Guid HolderId,
        string HolderName,
        string HolderType);

    private sealed record EnsureWallet(
        [property: JsonPropertyName("currencyID")] int CurrencyId,
        string Type,
        string Note);
}
