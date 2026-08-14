using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.Partner;
using JeebGateway.service.ServiceWallet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using SwServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;

namespace JeebGateway.IntegrationTests;

/// <summary>W2-R03 — wallet SELECTION safety on the partner money path: inactive rejected, no wrong-currency
/// fallback, SYSTEM wallet resolved by Type not position. Asserted through the real service's behaviour.</summary>
public sealed class PartnerWalletPickWalletTests
{
    private static readonly Guid PartnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TargetHolderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OperatorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const int Currency = 1;
    private const int OtherCurrency = 999;

    // The exact Type tokens wallet-service DataFeed seeds onto the system holder's wallets.
    private const string SystemType = "__SYSTEM__";
    private const string SystemPrimaryType = "__SYSTEM__PRIMARY__";

    // ── inactive wallet is rejected ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivated_Wallet_In_Right_Currency_Is_Not_Spendable()
    {
        var wallet = Wallet(Currency, "main", isActive: false);
        var svc = Service(new StubWalletClient { HolderWallets = { wallet } });

        var act = async () => await svc.ExecuteTopupAsync(PartnerId, TargetHolderId, 10d, "idem-1", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    [Fact]
    public async Task Deactivated_Wallet_Is_Not_Reported_As_A_Provisioned_Target()
    {
        var svc = Service(new StubWalletClient { HolderWallets = { Wallet(Currency, "main", isActive: false) } });

        var result = await svc.ResolveJeeberTargetAsync(TargetHolderId, default);

        result.HasWallet.Should().BeFalse("a deactivated wallet is not provisioned capacity");
    }

    [Fact]
    public async Task Deactivated_Wallet_Balance_Is_Not_Surfaced_As_Spendable()
    {
        var svc = Service(new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "main", isActive: false, amount: 500d) },
        });

        var result = await svc.GetPartnerBalanceAsync(PartnerId, default);

        result.Balance.Should().Be(0d, "the only wallet is deactivated, so nothing is spendable");
    }

    [Fact]
    public async Task An_Active_Wallet_Is_Selected_Over_A_Deactivated_One_Earlier_In_The_List()
    {
        var active = Wallet(Currency, "main", isActive: true, amount: 42d);
        var stub = new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "main", isActive: false, amount: 500d), active },
        };

        var result = await Service(stub).GetPartnerBalanceAsync(PartnerId, default);

        result.Balance.Should().Be(42d);
    }

    // ── the wrong-currency fallback is gone ───────────────────────────────────────────────────

    [Fact]
    public async Task No_Currency_Match_Does_Not_Fall_Back_To_An_Arbitrary_First_Wallet()
    {
        var svc = Service(new StubWalletClient { HolderWallets = { Wallet(OtherCurrency, "main", true) } });

        var act = async () => await svc.ExecuteTopupAsync(PartnerId, TargetHolderId, 10d, "idem-2", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    [Fact]
    public async Task No_Currency_Match_Does_Not_Fall_Back_To_A_System_Wallet_In_Another_Currency()
    {
        // The exact live failure mode: the only wallet is a system wallet in a foreign currency.
        var svc = Service(new StubWalletClient { HolderWallets = { Wallet(OtherCurrency, SystemType, true) } });

        var act = async () => await svc.ExecuteTopupAsync(PartnerId, TargetHolderId, 10d, "idem-3", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    [Fact]
    public async Task Foreign_Currency_Wallet_Is_Not_Reported_As_A_Provisioned_Target()
    {
        var svc = Service(new StubWalletClient { HolderWallets = { Wallet(OtherCurrency, "main", true) } });

        var result = await svc.ResolveJeeberTargetAsync(TargetHolderId, default);

        result.HasWallet.Should().BeFalse();
    }

    // ── holder and system allow-lists are not interchangeable ─────────────────────────────────

    [Fact]
    public async Task A_Holder_May_Not_Spend_A_System_Typed_Wallet_In_The_Right_Currency()
    {
        var svc = Service(new StubWalletClient { HolderWallets = { Wallet(Currency, SystemType, true) } });

        var act = async () => await svc.ExecuteTopupAsync(PartnerId, TargetHolderId, 10d, "idem-4", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    // ── the system wallet is resolved by type, not by position ────────────────────────────────

    [Fact]
    public async Task System_Wallet_Is_Resolved_By_Type_Not_By_Position()
    {
        var decoy = Wallet(Currency, "main", isActive: true);            // position 0, right currency
        var systemWallet = Wallet(Currency, SystemType, isActive: true); // position 1, the real source
        var stub = new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "main", true) },
            SystemWallets = { decoy, systemWallet },
        };

        await Service(stub).CreditPartnerFromCashAsync(
            PartnerId, OperatorId, 25d, "idem-5", "cash at counter", default);

        var source = stub.LastInitiated!.Transactions.First().SourceWalletId;
        source.Should().Be(systemWallet.WalletId, "the system source is chosen by Type");
        source.Should().NotBe(decoy.WalletId, "position 0 must not decide the system source");
    }

    [Fact]
    public async Task System_Wallet_Selection_Skips_A_Deactivated_System_Wallet()
    {
        var inactive = Wallet(Currency, SystemType, isActive: false);
        var active = Wallet(Currency, SystemType, isActive: true);
        var stub = new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "main", true) },
            SystemWallets = { inactive, active },
        };

        await Service(stub).CreditPartnerFromCashAsync(
            PartnerId, OperatorId, 25d, "idem-6", "cash at counter", default);

        stub.LastInitiated!.Transactions.First().SourceWalletId.Should().Be(active.WalletId);
    }

    [Fact]
    public async Task System_Wallet_Resolves_On_The_Base_Currency_Token_Too()
    {
        // DataFeed types the base-currency system wallet differently, so both defaults must resolve.
        var systemWallet = Wallet(Currency, SystemPrimaryType, isActive: true);
        var stub = new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "main", true) },
            SystemWallets = { systemWallet },
        };

        await Service(stub).CreditPartnerFromCashAsync(
            PartnerId, OperatorId, 25d, "idem-11", "cash at counter", default);

        stub.LastInitiated!.Transactions.First().SourceWalletId.Should().Be(systemWallet.WalletId);
    }

    [Fact]
    public async Task System_Wallet_Guard_Throws_When_Only_A_Holder_Typed_Wallet_Exists()
    {
        var stub = new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "main", true) },
            SystemWallets = { Wallet(Currency, "main", true) },
        };

        var act = async () => await Service(stub).CreditPartnerFromCashAsync(
            PartnerId, OperatorId, 25d, "idem-7", "cash at counter", default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    [Fact]
    public async Task No_Money_Moves_When_Wallet_Selection_Rejects_Every_Candidate()
    {
        var stub = new StubWalletClient { HolderWallets = { Wallet(Currency, "main", isActive: false) } };

        var act = async () => await Service(stub).ExecuteTopupAsync(
            PartnerId, TargetHolderId, 10d, "idem-8", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
        stub.InitiateCount.Should().Be(0, "selection fails before the saga starts");
    }

    // ── holder spendability stays on the ONE ratified predicate (R-M1/G-01) ───────────────────

    [Fact]
    public async Task Holder_Selection_Rejects_A_Cod_Float_Leg_Like_The_Ratified_Predicate_Does()
    {
        // JeebWallet.SpendableWalletTypes denies cod_* everywhere else; selection must agree, or the
        // sufficiency guard and the partner path would disagree about what is spendable.
        var svc = Service(new StubWalletClient { HolderWallets = { Wallet(Currency, "cod_float", true) } });

        var act = async () => await svc.ExecuteTopupAsync(PartnerId, TargetHolderId, 10d, "idem-10", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    [Fact]
    public async Task Holder_Selection_Accepts_An_Unknown_But_Spendable_Type()
    {
        // Wallet.Type is an opaque free-form string with no enum in the contract, so an unrecognised
        // type must stay spendable — an invented allow-list here would strand real balances.
        var svc = Service(new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, "general", isActive: true, amount: 31d) },
        });

        var result = await svc.GetPartnerBalanceAsync(PartnerId, default);

        result.Balance.Should().Be(31d);
    }

    // ── documented posture: an unclassified (blank) Type degrades OPEN ─────────────────────────

    [Fact]
    public async Task Blank_Type_Degrades_Open_Because_The_Upstream_Vocabulary_Is_Unconfirmed()
    {
        var svc = Service(new StubWalletClient
        {
            HolderWallets = { Wallet(Currency, type: null, isActive: true, amount: 7d) },
        });

        var result = await svc.GetPartnerBalanceAsync(PartnerId, default);

        result.Balance.Should().Be(7d, "wallet-service types the field as optional with no enum");
    }

    [Fact]
    public async Task Blank_Type_Still_Does_Not_Bypass_The_Currency_Or_Active_Gates()
    {
        var svc = Service(new StubWalletClient
        {
            HolderWallets =
            {
                Wallet(OtherCurrency, type: null, isActive: true),
                Wallet(Currency, type: null, isActive: false),
            },
        });

        var act = async () => await svc.ExecuteTopupAsync(PartnerId, TargetHolderId, 10d, "idem-9", null, default);

        await act.Should().ThrowAsync<PartnerWalletException>();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Wallet Wallet(int currency, string? type, bool isActive, double amount = 100d)
        => new()
        {
            WalletId = Guid.NewGuid(),
            CurrencyID = currency,
            Amount = amount,
            Type = type!,
            IsActive = isActive,
        };

    private static PartnerWalletService Service(StubWalletClient wallet)
        => new(
            wallet,
            new StubOperationStore(),
            new StubAuditLog(),
            Options.Create(new PartnerWalletOptions { CurrencyId = Currency }),
            NullLogger<PartnerWalletService>.Instance);

    /// <summary>Offline stub — serves the configured wallet lists and captures the initiated saga request.</summary>
    private sealed class StubWalletClient : SwServiceWalletClient
    {
        private static readonly Guid HeaderId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        public List<Wallet> HolderWallets { get; } = new();
        public List<Wallet> SystemWallets { get; } = new();
        public TransactionRequest? LastInitiated { get; private set; }
        public int InitiateCount { get; private set; }

        public StubWalletClient() : base("http://localhost", new HttpClient())
        {
        }

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId)
            => Task.FromResult(new GetHolderWallets
            {
                WalletHolder = new WalletHolder { HolderId = holderId, HolderName = "stub", IsActive = true },
                Wallets = HolderWallets,
            });

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
            => WalletsAsync(holderId);

        public override Task<AddWalletHolderResponse> SystemWalletAsync()
            => Task.FromResult(new AddWalletHolderResponse { Wallets = SystemWallets });

        public override Task<AddWalletHolderResponse> SystemWalletAsync(CancellationToken ct)
            => SystemWalletAsync();

        public override Task<ExpectedTransaction> PredictAsync(TransactionRequest body)
            => Task.FromResult(new ExpectedTransaction { GrossAmount = 0, Fees = 0, Summary = "stub" });

        public override Task<ExpectedTransaction> PredictAsync(TransactionRequest body, CancellationToken ct)
            => PredictAsync(body);

        public override Task<Transaction> InitiateAsync(TransactionRequest body)
        {
            InitiateCount++;
            LastInitiated = body;
            return Task.FromResult(new Transaction
            {
                TransactionHeader = new TransactionHeader { TxId = HeaderId, Status = 0 },
            });
        }

        public override Task<Transaction> InitiateAsync(TransactionRequest body, CancellationToken ct)
            => InitiateAsync(body);

        public override Task ExecuteAsync(Guid transactionHeaderId) => Task.CompletedTask;
        public override Task ExecuteAsync(Guid transactionHeaderId, CancellationToken ct) => Task.CompletedTask;
        public override Task AbortAsync(Guid transactionHeaderId) => Task.CompletedTask;
        public override Task AbortAsync(Guid transactionHeaderId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Always grants the claim — these tests assert selection, not idempotency.</summary>
    private sealed class StubOperationStore : IPartnerWalletOperationStore
    {
        public Task<PartnerOperationClaim> TryClaimAsync(
            PartnerOperationKey key, PartnerOperationIntent intent, CancellationToken ct)
            => Task.FromResult(new PartnerOperationClaim(PartnerClaimKind.Won, null));

        public Task CompleteAsync(
            PartnerOperationKey key, Guid transactionId, PartnerWalletMoveResponse result, CancellationToken ct)
            => Task.CompletedTask;

        public Task ReleaseAsync(PartnerOperationKey key, CancellationToken ct) => Task.CompletedTask;

        public Task MarkUncertainAsync(PartnerOperationKey key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubAuditLog : IAdminAuditLog
    {
        public Task<AdminAuditEntry> AppendAsync(AdminAuditAppend entry, CancellationToken ct)
            => Task.FromResult(new AdminAuditEntry
            {
                Id = Guid.NewGuid().ToString(),
                AdminUserId = entry.AdminUserId,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        public Task<IReadOnlyList<AdminAuditEntry>> ListForEntityAsync(
            string entityType, string entityId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AdminAuditEntry>>(Array.Empty<AdminAuditEntry>());
    }
}
