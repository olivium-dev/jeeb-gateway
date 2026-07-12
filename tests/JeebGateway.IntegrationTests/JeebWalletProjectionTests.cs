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
    private static Wallet ActiveWallet(double amount) =>
        new() { IsActive = true, Amount = amount, CurrencyID = 1 };

    private static Wallet InactiveWallet(double amount) =>
        new() { IsActive = false, Amount = amount, CurrencyID = 1 };

    private static GetHolderWallets Holder(params Wallet[] wallets) =>
        new() { WalletHolder = new WalletHolder(), Wallets = new List<Wallet>(wallets) };

    [Fact]
    public void ProjectBalance_Null_Holder_Is_Empty_Wallet()
    {
        var view = JeebWalletProjection.ProjectBalance(null);

        view.AvailableBalance.Should().Be(0);
        view.ReservedNow.Should().Be(0);
        view.GiftCredit.Should().Be(0);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Empty);
    }

    [Fact]
    public void ProjectBalance_Sums_Only_Active_Wallets()
    {
        var view = JeebWalletProjection.ProjectBalance(
            Holder(ActiveWallet(30), ActiveWallet(70), InactiveWallet(999)));

        view.AvailableBalance.Should().Be(100);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Enough);
    }

    [Fact]
    public void ProjectBalance_Zero_Available_Is_Empty_Affordability()
    {
        var view = JeebWalletProjection.ProjectBalance(Holder(InactiveWallet(500)));

        view.AvailableBalance.Should().Be(0);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Empty);
    }

    [Fact]
    public void ProjectBalance_Small_Positive_Balance_Is_Low_Affordability()
    {
        var view = JeebWalletProjection.ProjectBalance(Holder(ActiveWallet(5)));

        view.AvailableBalance.Should().Be(5);
        view.AffordabilityState.Should().Be(JeebWalletProjection.Affordability.Low);
    }

    [Fact]
    public void ProjectBalance_Currency_Is_Null_So_Mobile_Applies_Its_Default()
    {
        // The generic wallet row exposes only a numeric CurrencyID, not an ISO code;
        // the gateway must not fabricate one (it would be domain state). Null lets the
        // mobile parser apply its documented default.
        var view = JeebWalletProjection.ProjectBalance(Holder(ActiveWallet(50)));

        view.Currency.Should().BeNull();
    }

    // ----- JEBV4-49 (M4): money is decimal end-to-end in the wallet read projection -----

    [Fact]
    public void ProjectBalance_Fractional_Cents_Are_Preserved_As_Decimal_In_Json()
    {
        // A balance with cents must serialize as a clean decimal (no double
        // fractional artifact) on the display contract.
        var view = JeebWalletProjection.ProjectBalance(Holder(ActiveWallet(10.25), ActiveWallet(0.50)));

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
