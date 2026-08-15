using System.Linq;
using FluentAssertions;
using JeebGateway.Cms;
using JeebGateway.Migration;
using JeebGateway.StateService.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Cms;

/// <summary>
/// ADR-0008 — the gwdbx CMS-config leg is SUPERSEDED, not blocked. bundler-service owns every
/// surface, draft and publication row, so the gateway already meets the program mandate ("the
/// gateway owns no CMS state") by a different route than the plan wrote, and the state-service
/// ladder for this domain is pinned to "local" so nobody can flip a cutover that has no read path.
/// </summary>
public class CmsConfigLegSupersededTests
{
    // UseSetting: Program.cs reads these while it is still composing the container.
    private static WebApplicationFactory<Program> FactoryWith(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseDefaultServiceProvider(o => o.ValidateOnBuild = false);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [Fact]
    public void Mode_Defaults_To_The_Pinned_Local_Rung()
    {
        new GwdbxMigrationOptions().CmsConfigMode.Should().Be("local");
        new GwdbxMigrationOptions().CmsConfig.Should().Be(GwdbxMigrationPhase.Local);
    }

    /// <summary>The green-no-op trap this program was already bitten by: every rung above local
    /// would claim state-service is the CMS authority while nothing reads it. Refuse the boot.</summary>
    [Theory]
    [InlineData("dual-write-local-read")]
    [InlineData("dual-write-upstream-read")]
    [InlineData("upstream-authority")]
    [InlineData("retired")]
    public void Every_Rung_Above_Local_Refuses_To_Boot(string rung)
    {
        using var factory = FactoryWith(("FeatureFlags:CmsConfigMode", rung));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*CmsConfigMode*", "the failure must name the flag an operator has to fix");
    }

    /// <summary>The freeze-import leg is DELETED: replaying bundler's documents into state-service
    /// would fork the catalog into two independently writable owners with no reconciler.</summary>
    [Fact]
    public void Neither_The_Freeze_Import_Nor_The_Parity_Check_Has_A_Cms_Leg()
    {
        typeof(StateServiceConfigImporter).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(ICmsSurfaceStore));

        typeof(ConfigParityChecker).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(ICmsSurfaceStore));
    }

    /// <summary>The mandate itself: the CMS surface resolves to the stateless bundler adapter,
    /// so the gateway owns no CMS row at the pinned rung.</summary>
    [Fact]
    public void The_Cms_Store_Is_The_Stateless_Bundler_Adapter()
    {
        using var factory = FactoryWith();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICmsSurfaceStore>()
            .Should().BeOfType<BundlerCmsSurfaceStore>();
    }
}
