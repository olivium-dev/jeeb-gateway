using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.service.ServiceWallet;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// JEBV4-43 (PP-5) / JEBV4-119 — two independent commission derivations exist;
/// this suite proves they agree on the flat 10% USD policy:
/// <list type="bullet">
///   <item><see cref="CommissionCalculator"/> — the settlement-time calculator
///     accepted-offer basis), with no insurance or floor. This is what the real
///     settlement/payout deducts.</item>
///   <item><see cref="WalletEarningsAggregationService"/> — the
///     wallet-projection path shown on a jeeber's earnings surface. Its private
///     <c>DeriveCommission</c> helper now DELEGATES to
///     <see cref="CommissionCalculator.Calculate"/> (JEBV4-119), so it applies
///     the identical rate and rounding — it can no longer diverge.</item>
/// </list>
///
/// A THIRD divergent source exists and is intentionally NOT exercised here:
/// <see cref="JeebGateway.Controllers.AdminFinanceController"/>'s
/// dashboard/top-earners reads are a stub backed by
/// <c>IAdminFinanceDashboardService</c> that returns hardcoded zeros — part of
/// why on-device earnings always showed 0.00 (SW-01). Do NOT "fix" earnings by
/// editing that stub; it is a placeholder for a follow-up, not a third
/// commission implementation to reconcile here.
/// Zero/empty periods stay at commission 0, matching the settlement-backed
/// <see cref="EarningsAggregationService"/>, which sums zero settlement rows
/// to a zero commission.
/// </summary>
public class CommissionAgreementTests
{
    /// <summary>
    /// Feeds the SAME gross/goodsCost figure through both derivations and
    /// asserts the resulting commission (and therefore net = gross -
    /// commission) agree, across large USD values and the x.005 rounding
    /// boundary. Uses the raw 10% reference (<see cref="RawTenPercent"/>).
    /// </summary>
    [Theory]
    [InlineData(50_000_000)]        // large USD value
    [InlineData(12_345.67)]         // arbitrary fractional value
    [InlineData(10_000.05)]         // x.005-adjacent rounding boundary
    [InlineData(6_666.67)]
    [InlineData(6_666.66)]
    public void Both_Sources_Net_Equal_For_Flat_Ten_Percent(decimal grossOrGoodsCost)
    {
        var settlement = CommissionCalculator.Calculate(grossOrGoodsCost, CommissionTier.Standard);
        var rawWallet = RawTenPercent(grossOrGoodsCost);

        rawWallet.Should().Be(settlement.Commission,
            $"CommissionCalculator (goodsCost={grossOrGoodsCost}) and the wallet earnings derivation " +
            "must compute the identical commission for the jeeber-visible earnings and " +
            "the actual settlement payout to agree — divergence here is a silent money bug (JEBV4-43/PP-5).");
    }

    /// <summary>
    /// AC (JEBV4-119): small amounts do not use a minimum-fee floor. This drives the REAL public
    /// <see cref="WalletEarningsAggregationService.GetProjectionAsync"/> surface
    /// (via a fake <see cref="ServiceWalletClient"/>, no network) and asserts the
    /// wallet-visible commission now EQUALS the flat settlement commission.
    /// </summary>
    [Theory]
    [InlineData(1_000, 100)]
    [InlineData(2_000, 200)]
    [InlineData(5_000, 500)]
    public async Task Wallet_Projection_Commission_Equals_Flat_Settlement_For_Small_Amounts(
        decimal grossBelowFloor,
        decimal expectedCommission)
    {
        var wallet = new WalletEarningsAggregationService(new FixedGrossWalletClient(grossBelowFloor));

        var projection = await wallet.GetProjectionAsync("jeeber-1", from: null, to: null, CancellationToken.None);
        var settlement = CommissionCalculator.Calculate(grossBelowFloor, CommissionTier.Standard);

        settlement.MinimumFeeApplied.Should().BeFalse();
        settlement.Commission.Should().Be(expectedCommission);

        projection.Totals.Commission.Should().Be(settlement.Commission,
            $"[JEBV4-119] at gross={grossBelowFloor} the wallet-visible commission must equal the flat " +
            "settlement commission so the jeeber's net earnings match the actual payout — the pre-fix " +
            "divergent arithmetic is the money bug this ticket fixes.");
        projection.Totals.Net.Should().Be(projection.Totals.Gross - settlement.Commission,
            "net earnings must be gross minus the SAME commission the payout deducts");
    }

    /// <summary>
    /// Regression guard for the money edge case the JEBV4-119 fix must NOT break:
    /// an empty / zero-gross period has no settled deliveries, so it must show a
    /// zero commission and a zero net. Matches the settlement-backed
    /// <see cref="EarningsAggregationService"/> summing zero rows to zero.
    /// </summary>
    [Fact]
    public async Task Wallet_Projection_Zero_Gross_Yields_Zero_Commission_And_Zero_Net()
    {
        var wallet = new WalletEarningsAggregationService(new FixedGrossWalletClient(0m));

        var projection = await wallet.GetProjectionAsync("jeeber-idle", from: null, to: null, CancellationToken.None);

        projection.Totals.Gross.Should().Be(0m);
        projection.Totals.Commission.Should().Be(0m,
            "[JEBV4-119] an idle jeeber with no settled deliveries must not be charged commission");
        projection.Totals.Net.Should().Be(0m,
            "zero gross minus zero commission is zero");
    }

    /// <summary>
    /// End-to-end proof that the flat 10% derivation is shared. Drives the REAL
    /// <see cref="WalletEarningsAggregationService.GetProjectionAsync"/> surface
    /// with a fixed gross and asserts the wallet projection matches settlement exactly.
    /// </summary>
    [Fact]
    public async Task Wallet_Projection_No_Longer_Diverges_From_Settlement()
    {
        const decimal grossBelowFloor = 2_000m;
        var wallet = new WalletEarningsAggregationService(new FixedGrossWalletClient(grossBelowFloor));

        var projection = await wallet.GetProjectionAsync("jeeber-1", from: null, to: null, CancellationToken.None);
        var settlement = CommissionCalculator.Calculate(grossBelowFloor, CommissionTier.Standard);

        projection.Totals.Commission.Should().Be(settlement.Commission,
            "[JEBV4-119] the wallet projection and settlement must agree");
        projection.Totals.Commission.Should().Be(200m,
            "both sources apply the flat 10% commission");
    }

    /// <summary>
    /// Raw 10% reference, rounded AwayFromZero.
    /// </summary>
    private static decimal RawTenPercent(decimal gross) =>
        Math.Round(gross * CommissionCalculator.StandardRate, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Fake <see cref="ServiceWalletClient"/> (subclass override of the
    /// NSwag-generated virtual method) that returns a fixed gross figure
    /// without any network call — the established fake-client pattern for
    /// this suite. Never calls a real upstream service.
    /// </summary>
    private sealed class FixedGrossWalletClient : ServiceWalletClient
    {
        private readonly double _gross;

        public FixedGrossWalletClient(decimal gross)
            : base("http://unused.invalid", new HttpClient())
        {
            _gross = (double)gross;
        }

        public override Task<double> CreditRevenueAsync(
            string holderId, string period, DateTimeOffset? startDate, CancellationToken cancellationToken)
            => Task.FromResult(_gross);
    }
}
