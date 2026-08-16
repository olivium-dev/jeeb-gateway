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

    /// <summary>ADR-0010 retired the whole freeze-import trio, which is strictly stronger than the
    /// old "no ICmsSurfaceStore parameter" guard: the types themselves are gone.</summary>
    [Theory]
    [InlineData("JeebGateway.StateService.Config.StateServiceConfigImporter")]
    [InlineData("JeebGateway.StateService.Config.ConfigParityChecker")]
    [InlineData("JeebGateway.StateService.Config.ConfigImportWorker")]
    public void The_Freeze_Import_Trio_Is_Gone_From_The_Gateway_Assembly(string typeName)
    {
        (typeof(Program).Assembly.GetType(typeName) is null)
            .Should().BeTrue("ADR-0010 deleted it; its source stores are process memory that local "
                + "authoring can no longer refill, so it could only replay zero rows");
    }

    /// <summary>The retirement is the hosted-service ratchet's only mover here: 19 -> 18.</summary>
    [Fact]
    public void No_Config_Import_Hosted_Service_Remains()
    {
        typeof(Program).Assembly.GetTypes()
            .Any(t => t.Namespace == "JeebGateway.StateService.Config"
                      && typeof(IHostedService).IsAssignableFrom(t))
            .Should().BeFalse("the config namespace keeps its DTOs and loses its worker");
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
