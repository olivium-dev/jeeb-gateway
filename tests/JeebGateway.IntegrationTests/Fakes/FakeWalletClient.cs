using JeebGateway.service.ServiceWallet;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>F1 wallet-service double: generous default balance so the guards never trip
/// unless a test lowers <see cref="Balance"/> or sets <see cref="Unreachable"/>.</summary>
public sealed class FakeWalletClient : ServiceWalletClient
{
    public double Balance { get; set; } = 1_000_000;
    public int CurrencyId { get; set; } = 2;
    public bool Unreachable { get; set; }
    public bool CurrenciesUnreachable { get; set; }
    public int WalletReads { get; private set; }
    public ICollection<Currency> Currencies { get; set; } = new List<Currency>
    {
        new() { Id = 1, Code = "Credit", Rate = 0.1 },
        new() { Id = 2, Code = "USD", Rate = 1 },
    };

    public FakeWalletClient() : base("http://localhost", new HttpClient())
    {
    }

    public override Task<GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
    {
        WalletReads++;
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

    public override Task<ICollection<Currency>> CurrenciesAsync(CancellationToken ct)
        => CurrenciesUnreachable
            ? throw new HttpRequestException("simulated currency metadata outage")
            : Task.FromResult(Currencies);

    public override Task<ICollection<Currency>> CurrenciesAsync()
        => CurrenciesAsync(CancellationToken.None);
}
