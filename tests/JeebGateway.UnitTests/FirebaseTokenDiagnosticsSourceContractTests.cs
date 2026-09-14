using System.Reflection;
using System.Text.Json;
using JeebGateway.Auth.FirebaseDiagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class FirebaseTokenDiagnosticsSourceContractTests
{
    [Fact]
    public void Public_route_has_versioned_and_compatibility_twins()
    {
        var routes = typeof(FirebaseTokenDiagnosticsController)
            .GetCustomAttributes<RouteAttribute>()
            .Select(attribute => attribute.Template)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var action = typeof(FirebaseTokenDiagnosticsController).GetMethod("Verify")!;

        Assert.Equal(new[] { "auth", "v1/auth" }, routes);
        Assert.Equal(
            "diagnostics/firebase-token",
            action.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            20 * 1024,
            ((IRequestSizeLimitMetadata)action.GetCustomAttribute<RequestSizeLimitAttribute>()!)
            .MaxRequestBodySize);
        Assert.NotNull(typeof(FirebaseTokenDiagnosticsController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Controller_has_no_session_or_user_mutation_dependencies()
    {
        var constructor = Assert.Single(typeof(FirebaseTokenDiagnosticsController).GetConstructors());
        var dependencyNames = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.FullName ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(dependencyNames, name => name.Contains("TokenService", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencyNames, name => name.Contains("UsersStore", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencyNames, name => name.Contains("Logger", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_committed_configuration_keeps_diagnostic_disabled()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(repositoryRoot, "src", "JeebGateway"),
                     "appsettings*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Auth", out var auth)
                || !auth.TryGetProperty("FirebaseTokenDiagnostics", out var diagnostics))
            {
                continue;
            }

            Assert.False(diagnostics.GetProperty("Enabled").GetBoolean(), path);
        }
    }

    [Fact]
    public void Dedicated_transport_has_no_logger_or_resilience_retry_pipeline()
    {
        var repositoryRoot = FindRepositoryRoot();
        var clientSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "JeebGateway",
            "Auth",
            "FirebaseDiagnostics",
            "UserManagementFirebaseTokenDiagnosticClient.cs"));
        var programSource = File.ReadAllText(Path.Combine(repositoryRoot, "src", "JeebGateway", "Program.cs"));
        var registrationStart = programSource.IndexOf(
            "UserManagementFirebaseTokenDiagnosticClient.HttpClientName",
            StringComparison.Ordinal);
        var registrationEnd = programSource.IndexOf(
            "builder.Services.AddScoped<JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient>",
            registrationStart,
            StringComparison.Ordinal);
        var registration = programSource[registrationStart..registrationEnd];

        Assert.DoesNotContain("ILogger", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Log", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachResilience", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachStandardPipeline", registration, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_staging_deploy_pins_the_exact_diagnostic_pair_while_production_does_not_enable_it()
    {
        var repositoryRoot = FindRepositoryRoot();
        var staging = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "jeeb-staging-deploy.yml"));
        var production = File.ReadAllText(Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "jeeb-production-deploy.yml"));

        Assert.Contains("add_env Auth__FirebaseTokenDiagnostics__Enabled true", staging, StringComparison.Ordinal);
        Assert.Contains("add_env Auth__FirebaseTokenDiagnostics__Environment staging", staging, StringComparison.Ordinal);
        Assert.Contains("add_env Auth__FirebaseTokenDiagnostics__ProjectId jeeb-5a293", staging, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth__FirebaseTokenDiagnostics__Enabled true", production, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth__FirebaseTokenDiagnostics__Enabled=true", production, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
