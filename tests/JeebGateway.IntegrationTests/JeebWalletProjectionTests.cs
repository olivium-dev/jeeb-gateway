using System.Collections.Generic;
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
}
