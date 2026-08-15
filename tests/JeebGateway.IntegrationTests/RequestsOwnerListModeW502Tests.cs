using FluentAssertions;
using JeebGateway.Migration;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// W5-02 ladder contract for <c>FeatureFlags:RequestsOwnerListMode</c>. Requests move to
/// delivery-service by freeze-import-flip, so the dual-write rungs are REFUSED at boot
/// rather than merely unused: with the GatewayPostgres seam deleted the gateway's local
/// leg is in-memory only, so a dual-write-local-read rung would read from a store that
/// empties on restart — manufacturing the data loss it exists to prevent.
/// </summary>
public sealed class RequestsOwnerListModeW502Tests
{
    // Mirrors the Program.cs Validate predicate exactly.
    private static bool OnlyLocalOrAuthority(string mode) =>
        GwdbxMigrationOptions.PhaseOf(mode)
            is GwdbxMigrationPhase.Local or GwdbxMigrationPhase.UpstreamAuthority;

    [Fact]
    public void Ships_inert()
    {
        new GwdbxMigrationOptions().RequestsOwnerList.Should().Be(
            GwdbxMigrationPhase.Local,
            "the code default must not flip ownership on merge");
    }

    [Theory]
    [InlineData("local")]
    [InlineData("upstream-authority")]
    public void Accepts_the_two_freeze_import_flip_rungs(string mode)
        => OnlyLocalOrAuthority(mode).Should().BeTrue();

    [Theory]
    [InlineData("dual-write-local-read")]
    [InlineData("dual-write-upstream-read")]
    public void Refuses_every_dual_write_rung(string mode)
        => OnlyLocalOrAuthority(mode).Should().BeFalse(
            "there is no dual-write decorator for requests; the rung would claim a "
            + "mirroring that does not exist, and its local leg is in-memory only");

    [Fact]
    public void Unknown_values_are_rejected_rather_than_degrading_to_local()
    {
        GwdbxMigrationOptions.IsKnown("upstream-authority").Should().BeTrue();
        GwdbxMigrationOptions.IsKnown("").Should().BeFalse();
        GwdbxMigrationOptions.IsKnown("upstream").Should().BeFalse(
            "a near-miss spelling must fail the host loudly, not silently serve local");
        GwdbxMigrationOptions.IsKnown("UPSTREAM-AUTHORITY").Should().BeTrue(
            "casing is not what makes a mode wrong");
    }

    /// <summary>
    /// The ladder value is read from its OWN key. A copy-paste that bound requests to
    /// TiersMode would flip two migrations with one env var.
    /// </summary>
    [Fact]
    public void Reads_its_own_key_not_the_tiers_key()
    {
        var opts = new GwdbxMigrationOptions
        {
            RequestsOwnerListMode = "upstream-authority",
            TiersMode = "local",
        };

        opts.RequestsOwnerList.Should().Be(GwdbxMigrationPhase.UpstreamAuthority);
        opts.Tiers.Should().Be(GwdbxMigrationPhase.Local);
    }
}
