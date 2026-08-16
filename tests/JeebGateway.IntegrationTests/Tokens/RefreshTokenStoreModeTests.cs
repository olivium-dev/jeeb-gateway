using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.StateService;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

/// <summary>gwdbx W1-14 (A7/A10) — the refresh-token store is selected by the ONE ordered
/// <c>FeatureFlags:RefreshTokenStoreMode</c> rung on the shared gwdbx ladder, and FAILS CLOSED.</summary>
public class RefreshTokenStoreModeTests
{
    // UseSetting (not ConfigureAppConfiguration): the store is selected while Program.cs runs,
    // and only host settings are visible to those reads.
    // The FRAMEWORK factory: this suite's shadowing one replaces IRefreshTokenStore with the
    // in-memory store on every boot, which would make every rung on the ladder look identical.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> FactoryWith(
        params (string Key, string Value)[] settings) =>
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

    // Non-placeholder, >=32 bytes: gets a Production boot past JwtSigningKeyGuard so the
    // refresh-store guard is what aborts it, not the signing key.
    private const string ProductionSigningKey = "w1-14-refresh-store-boot-gate-0123456789abcdef";

    /// <summary>THE W1-14 CHARTER TEST — at the PINNED DEFAULT rung, a Production boot whose
    /// state-service BaseUrl was dropped ABORTS, and aborts because nothing is registered to fall
    /// back to. Byte-exact on the guard's wording, so re-adding an unconditional in-memory
    /// registration (which reports "resolved to 'InMemoryRefreshTokenStore'") fails this test.</summary>
    [Fact]
    public void Production_Boot_Without_StateService_Aborts_With_No_InMemory_Fallback_Registered()
    {
        // appsettings.Production.json COMMITS a state-service BaseUrl, so "unwired in Production"
        // has to be reproduced by blanking it — the dropped-env-var deploy this guard exists for.
        using var factory = ProductionFactory((StateServiceOptionsFactory.BaseUrlKey, ""));

        var boot = Record.Exception(() => factory.CreateClient());

        boot.Should().NotBeNull("a Production gateway must refuse to serve refresh tokens from memory");
        var detail = Flatten(boot!);
        detail.Should().Contain("IRefreshTokenStore", "the abort must name the store it could not resolve");
        detail.Should().NotContain(
            nameof(InMemoryRefreshTokenStore),
            "no in-memory refresh store may exist in a prod-like container — re-adding the "
            + "unconditional registration makes the boot survive to serve tokens from memory");
    }

    /// <summary>The other half of the charter: WITH state-service wired (the live shape), the
    /// state-service store is the resolution — so the abort above is the missing dependency only.</summary>
    [Fact]
    public void Production_Boot_With_StateService_Wired_Resolves_The_StateService_Store()
    {
        using var factory = ProductionFactory();

        var boot = Record.Exception(() => factory.CreateClient());

        Flatten(boot ?? new Exception(string.Empty)).Should().NotContain(
            "IRefreshTokenStore",
            "the committed Production BaseUrl wires the durable store; only other stores may fail here");
    }

    // UseSetting, not ConfigureAppConfiguration: only host settings outrank appsettings.Production.json
    // for the reads Program.cs makes while it is still composing the container.
    private static WebApplicationFactory<Program> ProductionFactory(
        params (string Key, string Value)[] overrides) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseDefaultServiceProvider(o => o.ValidateOnBuild = false);
            b.UseSetting("Jwt:SigningKey", ProductionSigningKey);
            b.UseSetting("UmJwt:SigningKey", ProductionSigningKey);
            // Same reason as the signing keys: Production commits no Redis endpoint (A25), so
            // without this the Redis guard aborts first and the refresh-store guard is untested.
            b.UseSetting("Redis:ConnectionString", "127.0.0.1:6379");
            // Same reason again: the OTP application-id guard was added after this suite and
            // now aborts the Production boot first, leaving the refresh-store guard untested.
            b.UseSetting("Auth:Otp:ApplicationId", "refresh-store-boot-gate-otp-app");
            b.UseSetting(Fakes.ProductionMountedSecrets.StateServiceTokenFileKey,
                Fakes.ProductionMountedSecrets.StateServiceTokenFile);
            foreach (var (key, value) in overrides)
            {
                b.UseSetting(key, value);
            }
        });

    /// <summary>The Development/Testing fallback is deliberate and must survive: the same unwired
    /// boot outside a prod-like environment still resolves the in-memory store.</summary>
    [Fact]
    public void Testing_Boot_Without_StateService_Keeps_The_InMemory_Store()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Testing"));

        factory.Services.GetRequiredService<IRefreshTokenStore>()
            .Should().BeOfType<InMemoryRefreshTokenStore>();
    }

    private static string Flatten(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append(" | ");
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    text.Append(Flatten(inner)).Append(" | ");
                }
            }
        }
        return text.ToString();
    }
}
