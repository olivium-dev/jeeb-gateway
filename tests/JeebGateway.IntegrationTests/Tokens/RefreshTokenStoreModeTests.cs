using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

/// <summary>gwdbx W1-14 (A7/A10) — the refresh-token store is selected by the ONE ordered
/// <c>FeatureFlags:RefreshTokenStoreMode</c> rung on the shared gwdbx ladder, and FAILS CLOSED.</summary>
public class RefreshTokenStoreModeTests
{
    // UseSetting (not ConfigureAppConfiguration): the store is selected while Program.cs runs,
    // and only host settings are visible to those reads.
    private static WebApplicationFactory<Program> FactoryWith(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static GwdbxMigrationPhase Rung(string wire) =>
        new GwdbxMigrationOptions { RefreshTokenStoreMode = wire }.RefreshTokenStore;

    /// <summary>REVIEW OF #395: the refresh domain rides the SHARED A10 ladder
    /// (<see cref="GwdbxMigrationPhase"/>), not a second parallel enum that can drift from it.</summary>
    [Fact]
    public void Ladder_Is_The_Five_Ordered_A10_Rungs()
    {
        Rung("local").Should().Be(GwdbxMigrationPhase.Local);
        Rung("dual-write-local-read").Should().Be(GwdbxMigrationPhase.DualWriteLocalRead);
        Rung("dual-write-upstream-read").Should().Be(GwdbxMigrationPhase.DualWriteUpstreamRead);
        Rung("upstream-authority").Should().Be(GwdbxMigrationPhase.UpstreamAuthority);
        Rung("retired").Should().Be(GwdbxMigrationPhase.Retired);

        GwdbxMigrationOptions.IsKnown("upstream").Should()
            .BeFalse("unknown rungs are rejected, never guessed");

        new[]
        {
            GwdbxMigrationPhase.Local, GwdbxMigrationPhase.DualWriteLocalRead,
            GwdbxMigrationPhase.DualWriteUpstreamRead, GwdbxMigrationPhase.UpstreamAuthority,
            GwdbxMigrationPhase.Retired,
        }.Should().BeInAscendingOrder("the ladder is ordered local -> ... -> retired")
         .And.OnlyHaveUniqueItems();

        GwdbxMigrationOptions.RequiresUpstream(GwdbxMigrationPhase.Local).Should().BeFalse();
        GwdbxMigrationOptions.RequiresUpstream(GwdbxMigrationPhase.DualWriteLocalRead).Should().BeFalse();
        GwdbxMigrationOptions.RequiresUpstream(GwdbxMigrationPhase.DualWriteUpstreamRead).Should()
            .BeTrue("the read flip makes upstream the READ authority — reads must never fall back to memory");
        GwdbxMigrationOptions.RequiresUpstream(GwdbxMigrationPhase.UpstreamAuthority).Should().BeTrue();
        GwdbxMigrationOptions.RequiresUpstream(GwdbxMigrationPhase.Retired).Should().BeTrue();
    }

    /// <summary>Unset config is the pinned default: the ladder reads <c>local</c>.</summary>
    [Fact]
    public void Unset_Mode_Resolves_To_Local()
    {
        new GwdbxMigrationOptions().RefreshTokenStore.Should().Be(GwdbxMigrationPhase.Local);
        GwdbxMigrationOptions.PhaseOf(null).Should().Be(GwdbxMigrationPhase.Local);
    }

    /// <summary>DEFAULT PATH, unchanged by this PR: no mode set and no state-service wired
    /// still resolves the in-memory store.</summary>
    [Fact]
    public void Default_Config_Still_Registers_The_InMemory_Store()
    {
        using var factory = FactoryWith();

        var store = factory.Services.GetRequiredService<IRefreshTokenStore>();

        store.Should().BeOfType<InMemoryRefreshTokenStore>(
            "the default rung keeps today's registration; only an explicit flip changes the store");
    }

    /// <summary>FAIL CLOSED — an in-memory fallback here would fork refresh-token families
    /// across replicas and restarts, silently, behind a green health check.</summary>
    [Fact]
    public void UpstreamAuthority_Without_StateService_Refuses_To_Boot()
    {
        // ValidateOnBuild is off in Production, so this pins the operator-facing failure rather
        // than the DI-graph error that fires first under the test host's dev-like defaults.
        using var factory = FactoryWith(("FeatureFlags:RefreshTokenStoreMode", "upstream-authority"))
            .WithWebHostBuilder(b => b.UseDefaultServiceProvider(o => o.ValidateOnBuild = false));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*RefreshTokenStoreMode*",
                "the failure must name the flag an operator has to fix");
    }

    /// <summary>REGRESSION (review of #395): dual-write-upstream-read is the READ FLIP, so an
    /// unwired state-service must refuse the boot, not silently serve reads from process memory.</summary>
    [Fact]
    public void DualWriteUpstreamRead_Without_StateService_Refuses_To_Boot()
    {
        using var factory = FactoryWith(("FeatureFlags:RefreshTokenStoreMode", "dual-write-upstream-read"))
            .WithWebHostBuilder(b => b.UseDefaultServiceProvider(o => o.ValidateOnBuild = false));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*dual-write-upstream-read*",
                "the read flip must name the rung that requires the dependency");
    }

    /// <summary>An unknown rung is a typo, not a rollout state: ValidateOnStart rejects it.</summary>
    [Fact]
    public void Unknown_Mode_Refuses_To_Boot()
    {
        using var factory = FactoryWith(("FeatureFlags:RefreshTokenStoreMode", "upstream"));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*upstream-authority*", "the failure must list the accepted rungs");
    }

    /// <summary>upstream-authority WITH the dependency wired: the state-service store is the
    /// only IRefreshTokenStore registration — no in-memory fallback is left to lose to.</summary>
    [Fact]
    public void UpstreamAuthority_With_StateService_Wired_Resolves_The_StateService_Store()
    {
        using var factory = FactoryWith(
            ("FeatureFlags:RefreshTokenStoreMode", "upstream-authority"),
            ("JeebStateService:Enabled", "true"),
            ("JeebStateService:BaseUrl", "http://127.0.0.1:9"));

        var store = factory.Services.GetRequiredService<IRefreshTokenStore>();

        store.Should().BeOfType<StateServiceRefreshTokenStore>();
    }
}
