using JeebGateway.service.ServiceWallet;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>F1 wallet-service double: generous default balance so the guards never trip
/// unless a test lowers <see cref="Balance"/> or sets <see cref="Unreachable"/>.</summary>
public sealed class FakeWalletClient : ServiceWalletClient
{
    public double Balance { get; set; } = 1_000_000;
    public int CurrencyId { get; set; } = 1;
    public bool Unreachable { get; set; }

    public FakeWalletClient() : base("http://localhost", new HttpClient())
    {
    }

    public override Task<GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("simulated wallet-service outage");
        }

        return Task.FromResult(new GetHolderWallets
        {
            WalletHolder = new WalletHolder { HolderId = holderId, HolderName = "fake", IsActive = true },
            Wallets = new List<Wallet>
            {
                new()
                {
                    WalletId = Guid.NewGuid(), HolderId = holderId, CurrencyID = CurrencyId,
                    Amount = Balance, IsActive = true, Type = "main",
                },
            },
        });
    }

    public override Task<GetHolderWallets> WalletsAsync(Guid holderId)
        => WalletsAsync(holderId, CancellationToken.None);
}
