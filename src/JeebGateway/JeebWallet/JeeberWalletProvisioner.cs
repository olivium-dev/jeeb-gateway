using WalletApi = JeebGateway.service.ServiceWallet;

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

/// <summary>
/// Dev-only partner bootstrap boundary. It converges a real partner holder in wallet-service;
/// credentials are not exposed until this succeeds.
/// </summary>
public interface IPartnerWalletProvisioner
{
    Task EnsureAsync(Guid holderId, string holderName, CancellationToken ct);
}

public sealed class WalletProvisioningUnavailableException : Exception
{
    public WalletProvisioningUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class WalletServiceJeeberWalletProvisioner :
    IJeeberWalletProvisioner,
    IPartnerWalletProvisioner
{
    public const string HttpClientName = "wallet-holder-provisioning-api";

    private const string JeeberHolderType = "jeeber";
    private const string PartnerHolderType = "partner";
    private const string DefaultWalletType = "jeeb";
    private const string JeeberProvisioningNote = "gateway-jeeber-role-activation";
    private const string PartnerProvisioningNote = "devtool-partner-wallet-bootstrap";
    private readonly IHttpClientFactory _clients;

    public WalletServiceJeeberWalletProvisioner(IHttpClientFactory clients) => _clients = clients;

    public Task EnsureAsync(Guid holderId, CancellationToken ct) => EnsureAsync(
        holderId,
        holderId.ToString("D"),
        JeeberHolderType,
        JeeberProvisioningNote,
        enforceHolderType: true,
        ct);

    Task IPartnerWalletProvisioner.EnsureAsync(
        Guid holderId,
        string holderName,
        CancellationToken ct) => EnsureAsync(
            holderId,
            string.IsNullOrWhiteSpace(holderName) ? holderId.ToString("D") : holderName.Trim(),
            PartnerHolderType,
            PartnerProvisioningNote,
            enforceHolderType: true,
            ct);

    private async Task EnsureAsync(
        Guid holderId,
        string holderName,
        string holderType,
        string provisioningNote,
        bool enforceHolderType,
        CancellationToken ct)
    {
        if (holderId == Guid.Empty)
            throw new ArgumentException("The system holder cannot receive a user wallet.", nameof(holderId));

        var http = _clients.CreateClient(HttpClientName);
        var baseAddress = http.BaseAddress?.ToString()
            ?? throw new WalletProvisioningUnavailableException(
                "Wallet-service holder provisioning has no configured base address.");
        var client = new WalletApi.ServiceWalletClient(baseAddress, http);
        try
        {
            var currencies = await ReadCurrenciesAsync(client, ct);
            var current = await client.WalletsAsync(holderId, ct);
            var request = BuildEnsureRequest(
                holderId,
                holderName,
                holderType,
                provisioningNote,
                enforceHolderType,
                currencies,
                current);

            var ensured = await client.EnsureAsync(request, ct);

            VerifyReady(holderId, holderType, enforceHolderType, currencies, ensured);
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
        catch (Exception ex) when (ex is HttpRequestException or WalletApi.ApiException)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service holder provisioning failed.", ex);
        }
    }

    private static async Task<IReadOnlyList<WalletApi.Currency>> ReadCurrenciesAsync(
        WalletApi.ServiceWalletClient client,
        CancellationToken ct)
    {
        var currencies = (await client.CurrenciesAsync(ct)).ToArray();
        if (currencies.Length == 0
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

    private static WalletApi.CreateWalletOwnerDto BuildEnsureRequest(
        Guid holderId,
        string holderName,
        string holderType,
        string provisioningNote,
        bool enforceHolderType,
        IReadOnlyList<WalletApi.Currency> currencies,
        WalletApi.GetHolderWallets current)
    {
        var active = (current.Wallets ?? Array.Empty<WalletApi.Wallet>())
            .Where(wallet => wallet.IsActive)
            .ToArray();
        var requestedWallets = new List<WalletApi.AddWalletRequest>(currencies.Count);
        foreach (var currency in currencies)
        {
            var matches = active.Where(wallet => wallet.CurrencyID == currency.Id).ToArray();
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
            requestedWallets.Add(new WalletApi.AddWalletRequest
            {
                CurrencyID = currency.Id,
                Type = walletType,
                Note = provisioningNote,
            });
        }

        var holder = current.WalletHolder;
        if (holder is not null && holder.HolderId != holderId)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service returned a holder id that does not match the requested user.");
        }
        if (enforceHolderType && holder is not null && string.IsNullOrWhiteSpace(holder.HolderType))
        {
            throw new WalletProvisioningUnavailableException(
                "Existing holder metadata is missing its actor type.");
        }
        if (enforceHolderType
            && holder is not null
            && !string.Equals(holder.HolderType, holderType, StringComparison.OrdinalIgnoreCase))
        {
            throw new WalletProvisioningUnavailableException(
                $"Holder is already provisioned as '{holder.HolderType}', not '{holderType}'.");
        }

        return new WalletApi.CreateWalletOwnerDto
        {
            WalletHolder = new WalletApi.AddWalletHolderRequest
            {
                HolderId = holderId,
                HolderName = string.IsNullOrWhiteSpace(holder?.HolderName)
                    ? holderName
                    : holder.HolderName,
                HolderType = holder is null ? holderType : holder.HolderType,
            },
            Wallets = requestedWallets,
        };
    }

    private static void VerifyReady(
        Guid holderId,
        string expectedHolderType,
        bool enforceHolderType,
        IReadOnlyList<WalletApi.Currency> currencies,
        WalletApi.AddWalletHolderResponse response)
    {
        if (response.WalletHolder is null
            || response.WalletHolder.HolderId != holderId
            || !response.WalletHolder.IsActive)
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service did not return the expected active holder.");
        }
        if (enforceHolderType
            && !string.Equals(
                response.WalletHolder.HolderType,
                expectedHolderType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service returned a holder with the wrong actor type.");
        }

        var active = (response.Wallets ?? Array.Empty<WalletApi.Wallet>())
            .Where(wallet => wallet.IsActive)
            .ToArray();
        if (currencies.Any(currency =>
            {
                var matches = active.Where(wallet => wallet.CurrencyID == currency.Id).ToArray();
                return matches.Length != 1 || matches[0].WalletId == Guid.Empty;
            }))
        {
            throw new WalletProvisioningUnavailableException(
                "Wallet-service did not converge to exactly one active wallet per configured currency.");
        }
    }

}
