using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.JeebWallet;
using JeebGateway.service.ServiceWallet;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit coverage of the generic→Jeeb wallet BALANCE projection that lives in the
/// gateway (ADR-0001 thin map). These bypass HTTP/DI so the Jeeb presentation
/// vocabulary applied over the shared opaque wallet rows can be exercised directly
/// — mirroring <see cref="JeebRatingProjectionTests"/>.
/// </summary>
public class JeebWalletProjectionTests
{
    // OD-C3-5: the projection is pinned to the configured fee currency, so every
    // fixture sits on the repo default pair (id 2 = "USD").
    private const int FeeCurrencyId = 2;
    private const string FeeCurrencyCode = "USD";

    private static JeebWalletBalanceResponse Project(GetHolderWallets? holder) =>
        JeebWalletProjection.ProjectBalance(holder, FeeCurrencyId, FeeCurrencyCode);

    private static Wallet ActiveWallet(double amount) =>
        new() { IsActive = true, Amount = amount, CurrencyID = FeeCurrencyId };

    private static Wallet InactiveWallet(double amount) =>
        new() { IsActive = false, Amount = amount, CurrencyID = FeeCurrencyId };

    // Active + spendable, but NOT the fee currency (legacy Credit/1 rows are real).
    private static Wallet OtherCurrencyWallet(double amount) =>
        new() { IsActive = true, Amount = amount, CurrencyID = 1 };

    private static GetHolderWallets Holder(params Wallet[] wallets) =>
        new() { WalletHolder = new WalletHolder(), Wallets = new List<Wallet>(wallets) };

    [Fact]
    public void ProjectBalance_Null_Holder_Is_Empty_Wallet()
    {
        var view = Project(null);

        view.AvailableBalance.Should().Be(0);
        view.ReservedNow.Should().Be(0);
        view.GiftCredit.Should().Be(0);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Empty);
    }

    [Fact]
    public void ProjectBalance_Sums_Only_Active_Wallets()
    {
        var view = Project(Holder(ActiveWallet(30), ActiveWallet(70), InactiveWallet(999)));

        view.AvailableBalance.Should().Be(100);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Enough);
    }

    [Fact]
    public void ProjectBalance_Zero_Available_Is_Empty_Affordability()
    {
        var view = Project(Holder(InactiveWallet(500)));

        view.AvailableBalance.Should().Be(0);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Empty);
    }

    [Fact]
    public void ProjectBalance_Small_Positive_Balance_Is_Low_Affordability()
    {
        var view = Project(Holder(ActiveWallet(5)));

        view.AvailableBalance.Should().Be(5);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Low);
    }

    [Fact]
    public void ProjectBalance_Currency_Is_The_Configured_Fee_Code()
    {
        // The projection names the SAME currency the commission debits, so the code
        // is the configured fee code — never null, never fabricated per wallet row.
        var view = Project(Holder(ActiveWallet(50)));

        view.Currency.Should().Be(FeeCurrencyCode);
    }

    [Fact]
    public void ProjectBalance_Never_Combines_Currencies_In_Any_Sum()
    {
        // OD-C3-5: only fee-currency wallets contribute; a fatter foreign-currency
        // wallet must not inflate the balance (nor the affordability bucket).
        var view = Project(Holder(ActiveWallet(10), OtherCurrencyWallet(50)));

        view.AvailableBalance.Should().Be(10m);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Low);
    }

    // ----- R-M1 (G-01): cod_* float is never user-spendable balance -----

    private static Wallet TypedWallet(string? type, double amount) =>
        new() { IsActive = true, Amount = amount, CurrencyID = FeeCurrencyId, Type = type };

    [Theory]
    [InlineData("cod_earnings")]
    [InlineData("cod_commission")]
    [InlineData("cod_insurance")]
    [InlineData("COD_Earnings")]
    [InlineData(" cod_earnings")]
    public void ProjectBalance_Excludes_Cod_Float_From_AvailableBalance(string codType)
    {
        var view = Project(Holder(ActiveWallet(30), TypedWallet(codType, 5_000)));

        view.AvailableBalance.Should().Be(30);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Enough);
    }

    [Fact]
    public void ProjectBalance_Cod_Float_Alone_Projects_As_An_Empty_Wallet()
    {
        var view = Project(Holder(TypedWallet("cod_earnings", 5_000)));

        view.AvailableBalance.Should().Be(0);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("codex")]
    [InlineData("topup")]
    public void ProjectBalance_Still_Counts_Every_Non_Cod_Type(string? spendableType)
    {
        // Control case: the filter must not be a blanket exclusion. Untyped wallets are
        // what every already-provisioned holder has today and must keep counting.
        var view = Project(Holder(TypedWallet(spendableType, 42)));

        view.AvailableBalance.Should().Be(42);
    }

    // ----- JEBV4-49 (M4): money is decimal end-to-end in the wallet read projection -----

    [Fact]
    public void ProjectBalance_Fractional_Cents_Are_Preserved_As_Decimal_In_Json()
    {
        // A balance with cents must serialize as a clean decimal (no double
        // fractional artifact) on the display contract.
        var view = Project(Holder(ActiveWallet(10.25), ActiveWallet(0.50)));

        view.AvailableBalance.Should().Be(10.75m);
        JsonSerializer.Serialize(view).Should().Contain("\"availableBalance\":10.75");
    }

    [Fact]
    public void LedgerEntry_Amount_Is_Decimal_And_Preserves_Integer_Precision_Past_2pow53_In_Json()
    {
        // M4 core: a large LBP amount past 2^53 (9,007,199,254,740,993) would lose
        // its trailing integer as a double (→ ...992). With the DTO now decimal,
        // reading the NUMERIC ledger column straight through preserves it exactly.
        var entry = new JeebWalletLedgerEntry
        {
            Id = "tx1",
            Type = "topup",
            Amount = 9_007_199_254_740_993m,
            Sign = 1,
            Ref = "r1",
            Ts = "2026-07-12T00:00:00.0000000Z",
        };

        JsonSerializer.Serialize(entry).Should().Contain("\"amount\":9007199254740993");
    }

    [Fact]
    public void LedgerEntry_Amount_Serializes_Fractional_Value_Without_Double_Artifact()
    {
        var entry = new JeebWalletLedgerEntry { Id = "tx2", Type = "fee_won", Amount = 0.30m, Sign = -1, Ref = "r2", Ts = "t" };

        // 0.1 + 0.2 as double serializes as 0.30000000000000004; decimal stays 0.30.
        JsonSerializer.Serialize(entry).Should().Contain("\"amount\":0.30");
    }
}
