using JeebGateway.Availability;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.UnitTests;

// W3/c1 (G1, T1): the aggregate-exposure primitive — only live legs count, exclusions are
// honoured, and each leg's 10% is rounded independently (CONTRACT §1, AwayFromZero).
public class JeeberExposureCalculatorTests
{
    [Fact]
    public void SumLiveCommission_SumsOnlyLiveLegs()
    {
        var legs = new[]
        {
            new ExposureLeg("offer-live-1", "req-1", 100.00m, PendingOfferStatus.Pending),
            new ExposureLeg("offer-live-2", "req-2", 250.00m, PendingOfferStatus.Accepted),
            // Raw offer-service synonyms for pending — live on the wire, must be counted.
            new ExposureLeg("offer-live-3", "req-3", 40.00m, "submitted"),
            new ExposureLeg("offer-live-4", "req-4", 10.00m, "edited"),
            new ExposureLeg("offer-dead-1", "req-5", 1000.00m, PendingOfferStatus.Withdrawn),
            new ExposureLeg("offer-dead-2", "req-6", 2000.00m, PendingOfferStatus.Superseded),
            new ExposureLeg("offer-dead-3", "req-7", 3000.00m, "rejected"),
        };

        Assert.Equal(40.00m, JeeberExposureCalculator.SumLiveCommission(legs));
    }

    [Fact]
    public void SumLiveCommission_SumsZero_WhenNoLegIsLive()
    {
        var legs = new[]
        {
            new ExposureLeg("offer-dead-1", "req-1", 1000.00m, PendingOfferStatus.Withdrawn),
            new ExposureLeg("offer-dead-2", "req-2", 2000.00m, PendingOfferStatus.Superseded),
        };

        Assert.Equal(0m, JeeberExposureCalculator.SumLiveCommission(legs));
        Assert.Equal(0m, JeeberExposureCalculator.SumLiveCommission(Array.Empty<ExposureLeg>()));
    }

    [Fact]
    public void SumLiveCommission_ExcludesGivenOfferId()
    {
        var legs = new[]
        {
            new ExposureLeg("offer-a", "req-1", 100.00m, PendingOfferStatus.Pending),
            new ExposureLeg("offer-b", "req-2", 50.00m, PendingOfferStatus.Pending),
            new ExposureLeg("offer-c", "req-3", 30.00m, PendingOfferStatus.Accepted),
        };

        Assert.Equal(18.00m, JeeberExposureCalculator.SumLiveCommission(legs));
        // The edited/accepted offer itself is never double-counted against its own raise.
        Assert.Equal(13.00m, JeeberExposureCalculator.SumLiveCommission(legs, excludeOfferId: "offer-b"));
        Assert.Equal(18.00m, JeeberExposureCalculator.SumLiveCommission(legs, excludeOfferId: "offer-not-mine"));
    }

    [Fact]
    public void SumLiveCommission_ExcludesGivenRequestId()
    {
        var legs = new[]
        {
            new ExposureLeg("offer-a", "req-1", 100.00m, PendingOfferStatus.Pending),
            new ExposureLeg("offer-b", "req-1", 20.00m, PendingOfferStatus.Pending),
            new ExposureLeg("offer-c", "req-2", 30.00m, PendingOfferStatus.Pending),
        };

        // Accept path: every sibling leg on the accepted request drops out, not just the winner.
        Assert.Equal(3.00m, JeeberExposureCalculator.SumLiveCommission(legs, excludeRequestId: "req-1"));
        // Both req-1 legs survive: 10% of 100.00 + 10% of 20.00.
        Assert.Equal(12.00m, JeeberExposureCalculator.SumLiveCommission(legs, excludeRequestId: "req-2"));
    }

    [Fact]
    public void SumLiveCommission_RoundsPerLegAwayFromZero()
    {
        var legs = new[]
        {
            new ExposureLeg("offer-a", "req-1", 100.25m, PendingOfferStatus.Pending),
            new ExposureLeg("offer-b", "req-2", 100.25m, PendingOfferStatus.Pending),
        };

        var sum = JeeberExposureCalculator.SumLiveCommission(legs);

        // Per-leg 10.03 + 10.03 — matching how the collector debits per offer, NOT 20.05.
        Assert.Equal(20.06m, sum);
        Assert.NotEqual(20.05m, sum);
        Assert.Equal(20.05m, WalletGuardContract.RequiredCommission(200.50m));
    }
}
