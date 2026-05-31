using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.Wallet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Batch 1 thin-wire: <see cref="InAppWalletService"/> balance/transaction reads
/// must DELEGATE to the real wallet-service client (no more hardcoded 0/empty
/// stubs). These tests drive the service through a fake
/// <see cref="IWalletServiceClient"/> that returns a known holder snapshot and
/// assert the gateway projects real upstream data rather than fabricating zero.
/// </summary>
public class InAppWalletServiceDelegationTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task GetBalance_Sums_Active_Wallets_From_Upstream()
    {
        var client = new FakeWalletClient(new SystemWalletResponse
        {
            WalletHolder = new SystemWalletHolder { HolderId = Guid.Parse(UserId), IsActive = true },
            Wallets = new[]
            {
                new SystemWallet { Amount = 150_000m, Type = "main",    IsActive = true },
                new SystemWallet { Amount = 50_000m,  Type = "pending", IsActive = true },
                new SystemWallet { Amount = 999_000m, Type = "main",    IsActive = false }, // ignored
            }
        });
        var svc = new InAppWalletService(client, NullLogger<InAppWalletService>.Instance);

        var balance = await svc.GetBalanceAsync(UserId, CancellationToken.None);

        client.LastHolderId.Should().Be(UserId, "balance must be read from the real upstream by holder id");
        balance.Available.Should().Be(150_000m);
        balance.Pending.Should().Be(50_000m);
        balance.Currency.Should().Be("LBP");
    }

    [Fact]
    public async Task GetBalance_Returns_Zero_When_No_Holder_Provisioned()
    {
        var client = new FakeWalletClient(holder: null);
        var svc = new InAppWalletService(client, NullLogger<InAppWalletService>.Instance);

        var balance = await svc.GetBalanceAsync(UserId, CancellationToken.None);

        client.LastHolderId.Should().Be(UserId);
        balance.Available.Should().Be(0m);
        balance.Pending.Should().Be(0m);
    }

    [Fact]
    public async Task GetTransactions_Projects_Real_Wallets_And_Pages()
    {
        var wallets = Enumerable.Range(0, 5).Select(i => new SystemWallet
        {
            WalletId = Guid.NewGuid(),
            Amount = 1_000m * (i + 1),
            Type = "main",
            IsActive = true,
            Note = $"note-{i}",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, i, DateTimeKind.Utc),
        }).ToArray();

        var client = new FakeWalletClient(new SystemWalletResponse
        {
            WalletHolder = new SystemWalletHolder { HolderId = Guid.Parse(UserId), IsActive = true },
            Wallets = wallets,
        });
        var svc = new InAppWalletService(client, NullLogger<InAppWalletService>.Instance);

        var page1 = await svc.GetTransactionsAsync(UserId, page: 1, pageSize: 2, CancellationToken.None);
        var page2 = await svc.GetTransactionsAsync(UserId, page: 2, pageSize: 2, CancellationToken.None);

        client.LastHolderId.Should().Be(UserId, "transactions must be backed by a real upstream read");
        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);
        page1.Select(t => t.Id).Should().NotIntersectWith(page2.Select(t => t.Id),
            "pagination must not repeat rows across pages");
        page1.All(t => t.Currency == "LBP").Should().BeTrue();
    }

    private sealed class FakeWalletClient : IWalletServiceClient
    {
        private readonly SystemWalletResponse? _holder;
        public string? LastHolderId { get; private set; }

        public FakeWalletClient(SystemWalletResponse? holder) => _holder = holder;

        public Task<SystemWalletResponse?> GetHolderWalletsAsync(string holderId, CancellationToken ct)
        {
            LastHolderId = holderId;
            return Task.FromResult(_holder);
        }

        public Task<SystemWalletResponse?> GetSystemWalletAsync(CancellationToken ct)
            => Task.FromResult(_holder);

        public Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
            => Task.FromResult(new LedgerEntryResponse { LedgerEntryId = "x" });
    }
}
