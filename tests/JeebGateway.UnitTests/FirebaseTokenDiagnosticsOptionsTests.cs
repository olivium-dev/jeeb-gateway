using JeebGateway.Auth.FirebaseDiagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class FirebaseTokenDiagnosticsOptionsTests
{
    [Theory]
    [InlineData("Development", "development", "jeeb-development-msi", true)]
    [InlineData("Staging", "staging", "jeeb-5a293", true)]
    [InlineData("Production", "staging", "jeeb-5a293", false)]
    [InlineData("Development", "development", "jeeb-5a293", false)]
    [InlineData("Staging", "staging", "jeeb-development-msi", false)]
    [InlineData("Development", "staging", "jeeb-5a293", false)]
    [InlineData("Testing", "development", "jeeb-development-msi", false)]
    public void Allowlist_requires_exact_host_environment_profile_and_project(
        string hostEnvironment,
        string configuredEnvironment,
        string projectId,
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
            FirebaseTokenDiagnosticsOptions.IsAllowed(new TestHostEnvironment(hostEnvironment), options));
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
