using JeebGateway.Auth.FirebaseDiagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class FirebaseTokenDiagnosticsOptionsTests
{
    [Theory]
    [InlineData("Development", "development", "jeeb-development-msi", "workstation", true)]
    [InlineData("Staging", "staging", "jeeb-5a293", "staging-01", true)]
    [InlineData("Production", "staging", "jeeb-5a293", "ouday-GT70-2OC-2OD", false)]
    [InlineData("Production", "development", "jeeb-development-msi", "production-01", false)]
    [InlineData("Production", "development", "jeeb-development-msi", "ouday-GT70-2OC-2OD", true)]
    [InlineData("Development", "development", "jeeb-5a293", "workstation", false)]
    [InlineData("Staging", "staging", "jeeb-development-msi", "staging-01", false)]
    [InlineData("Development", "staging", "jeeb-5a293", "workstation", false)]
    [InlineData("Testing", "development", "jeeb-development-msi", "test-host", false)]
    public void Allowlist_requires_exact_host_environment_profile_and_project(
        string hostEnvironment,
        string configuredEnvironment,
        string projectId,
        string machineName,
        bool expected)
    {
        var options = new FirebaseTokenDiagnosticsOptions
        {
            Enabled = true,
            Environment = configuredEnvironment,
            ProjectId = projectId,
        };

        Assert.Equal(
            expected,
            FirebaseTokenDiagnosticsOptions.IsAllowed(
                new TestHostEnvironment(hostEnvironment),
                options,
                machineName));
    }

    [Fact]
    public void Disabled_is_rejected_even_for_an_allowed_pair()
    {
        var options = new FirebaseTokenDiagnosticsOptions
        {
            Enabled = false,
            Environment = "development",
            ProjectId = "jeeb-development-msi",
        };

        Assert.False(FirebaseTokenDiagnosticsOptions.IsAllowed(
            new TestHostEnvironment(Environments.Development),
            options));
    }

    internal sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "JeebGateway.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
