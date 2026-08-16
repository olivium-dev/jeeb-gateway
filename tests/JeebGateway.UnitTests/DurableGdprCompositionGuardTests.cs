using System.Reflection;
using JeebGateway.Controllers;
using JeebGateway.Jobs;
using Xunit;

namespace JeebGateway.UnitTests;

/// <summary>
/// DataExportController shipped depending on IDataExportWorkflow while nothing registered it, so
/// every GDPR data-export route answered 500 ("Unable to resolve service for type
/// IDataExportWorkflow while attempting to activate DataExportController") in production. A class
/// that compiles proves nothing about the composed application, so this guard reads Program.cs and
/// asserts each controller dependency is actually registered, and that the sweep is actually driven.
/// </summary>
public class DurableGdprCompositionGuardTests
{
    private static readonly string Program = ReadProgram();

    [Theory]
    [InlineData("IDataExportWorkflow")]
    [InlineData("IAccountDeletionWorkflow")]
    [InlineData("IStateWorkItemClient")]
    [InlineData("IPrivateArtifactStore")]
    [InlineData("IDataExportTokenProtector")]
    [InlineData("DurableWorkSweepExecutor")]
    public void Program_registers_every_durable_gdpr_dependency(string typeName)
    {
        // Anti-vacuity: the same search must MISS a name that is genuinely absent, otherwise a
        // trivially-true "Contains" would pass for anything.
        Assert.False(IsRegistered("INeverRegisteredDurableThing"));

        Assert.True(IsRegistered(typeName), $"Program.cs does not register {typeName}");
    }

    [Fact]
    public void Every_DataExportController_constructor_dependency_is_registered()
    {
        var parameters = typeof(DataExportController)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single()
            .GetParameters();

        // Anti-vacuity: the controller really does take dependencies to check.
        Assert.NotEmpty(parameters);

        foreach (var parameter in parameters)
        {
            Assert.True(
                IsRegistered(parameter.ParameterType.Name),
                $"{parameter.ParameterType.Name} is a DataExportController dependency but Program.cs "
                + "never registers it — activating the controller would throw a 500.");
        }
    }

    [Fact]
    public void The_durable_sweep_is_driven_in_process()
    {
        // A durable deadline nothing ever claims is still a deadline that never fires.
        Assert.Contains("AddHostedService<JeebGateway.Jobs.DurableWorkSweepWorker>", Program, StringComparison.Ordinal);
        Assert.Contains(DurableWorkContract.AccountDeletionKind, DefaultSweepKinds());
        Assert.Contains(DurableWorkContract.DataExportKind, DefaultSweepKinds());
    }

    [Fact]
    public void The_retired_in_memory_purge_worker_is_gone()
    {
        // It swept a store no request path writes to; leaving it registered would mean two
        // schedulers claiming authority over one legal clock.
        Assert.DoesNotContain("AccountDeletionPurgeWorker>()", Program, StringComparison.Ordinal);
        Assert.Null(typeof(DurableWorkContract).Assembly
            .GetType("JeebGateway.Users.AccountDeletionPurgeWorker"));
    }

    private static IReadOnlyList<string> DefaultSweepKinds() => new DurableWorkSweepOptions().Kinds;

    private static bool IsRegistered(string typeName) =>
        Program.Contains("AddSingleton<" + typeName, StringComparison.Ordinal)
        || Program.Contains("AddScoped<" + typeName, StringComparison.Ordinal)
        || Program.Contains("AddTransient<" + typeName, StringComparison.Ordinal)
        || Program.Contains("AddHttpClient<" + typeName, StringComparison.Ordinal)
        || Program.Contains("." + typeName + ",", StringComparison.Ordinal)
        || Program.Contains("." + typeName + ">", StringComparison.Ordinal);

    private static string ReadProgram()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "src/JeebGateway/Program.cs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src/JeebGateway/Program.cs"));
    }
}
