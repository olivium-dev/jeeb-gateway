using System.Reflection;
using JeebGateway.StateService.Config;
using Xunit;

namespace JeebGateway.UnitTests;

// R3-3: ADR-0010's retirement guard, ported out of JeebGateway.IntegrationTests
// (74 compile errors since W5-11) into the one test project that executes.
public class HostedServiceRetirementGuardTests
{
    private const string ConfigNamespace = "JeebGateway.StateService.Config";

    // Matched by NAME so the guard needs no compile-time hosting reference and still
    // sees types that inherit IHostedService through BackgroundService.
    private const string HostedServiceInterface = "Microsoft.Extensions.Hosting.IHostedService";

    private static readonly string[] RetiredTypeNames =
    {
        "ConfigImportWorker",
        "StateServiceConfigImporter",
        "ConfigParityChecker",
    };

    // A surviving DTO of the namespace under test anchors the assembly, so renaming or
    // emptying that namespace cannot turn these assertions into vacuous passes.
    private static readonly Assembly Gateway = typeof(ConfigSurfaceRecordV1).Assembly;

    [Theory]
    [InlineData("JeebGateway.StateService.Config.ConfigImportWorker")]
    [InlineData("JeebGateway.StateService.Config.StateServiceConfigImporter")]
    [InlineData("JeebGateway.StateService.Config.ConfigParityChecker")]
    public void The_freeze_import_trio_is_gone_from_the_gateway_assembly(string fullName)
    {
        // Control: the identical lookup still resolves a type that IS present.
        Assert.NotNull(Gateway.GetType(ConfigNamespace + ".ConfigSurfaceRecordV1"));

        Assert.Null(Gateway.GetType(fullName));
    }

    [Fact]
    public void The_retired_trio_is_not_resurrected_under_another_namespace()
    {
        var resurrected = LoadableTypes()
            .Where(t => RetiredTypeNames.Contains(t.Name))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(
            resurrected.Length == 0,
            "ADR-0010 retired these; found: " + string.Join(", ", resurrected));
    }

    [Fact]
    public void No_hosted_service_remains_in_the_state_service_config_namespace()
    {
        var inNamespace = LoadableTypes()
            .Where(t => t.Namespace == ConfigNamespace)
            .ToArray();

        // Anti-vacuity: the namespace still exists and still carries its config DTOs.
        Assert.NotEmpty(inNamespace);

        var hosted = inNamespace
            .Where(t => t.GetInterfaces().Any(i => i.FullName == HostedServiceInterface))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(
            hosted.Length == 0,
            "the config namespace keeps its DTOs and loses its worker; found: "
                + string.Join(", ", hosted));
    }

    private static Type[] LoadableTypes()
    {
        try
        {
            return Gateway.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = new List<Type>();
            foreach (var type in ex.Types)
            {
                if (type is not null)
                {
                    loaded.Add(type);
                }
            }

            return loaded.ToArray();
        }
    }
}
