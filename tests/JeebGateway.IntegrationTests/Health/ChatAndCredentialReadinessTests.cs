using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests.Health;

/// <summary>
/// Chat was 100% 503-able by a deploy-time flag while /health/ready stayed green on
/// both hosts, and six credentials defaulted to Swarm-only /run/secrets paths with no
/// readiness surface. These tests pin the visibility, not the plumbing.
/// </summary>
public sealed class ChatAndCredentialReadinessTests
{
    // ---------------------------------------------------------------- chat

    [Fact]
    public async Task Chat_flag_off_is_Degraded_not_Healthy()
    {
        var result = await ProbeChatAsync(chatEnabled: false, upstream: null);

        result.Status.Should().Be(HealthStatus.Degraded,
            "a deploy that turns chat off must be visible on /health/ready");
        result.Description.Should().Contain("chat disabled by flag");
    }

    [Fact]
    public async Task Chat_enabled_without_a_base_url_is_Degraded()
    {
        var result = await ProbeChatAsync(chatEnabled: true, upstream: null);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain(ChatUpstreamHealthCheck.BaseUrlConfigurationKey);
    }

    [Fact]
    public async Task Chat_firestore_probe_200_is_Healthy()
    {
        await using var upstream = await StubChatServiceAsync(
            firebase: HttpStatusCode.OK, check: HttpStatusCode.OK);

        var result = await ProbeChatAsync(chatEnabled: true, upstream.Url);

        result.Status.Should().Be(HealthStatus.Healthy);
        upstream.Hits.Should().Contain("/" + ChatUpstreamHealthCheck.FirestoreProbePath);
    }

    [Fact]
    public async Task Chat_falls_back_to_the_liveness_route_on_an_older_chat_service()
    {
        await using var upstream = await StubChatServiceAsync(
            firebase: HttpStatusCode.NotFound, check: HttpStatusCode.OK);

        var result = await ProbeChatAsync(chatEnabled: true, upstream.Url);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should()
            .Contain("404")
            .And.Contain(ChatUpstreamHealthCheck.LivenessProbePath)
            .And.Contain("UNVERIFIED");
        upstream.Hits.Should().Contain("/" + ChatUpstreamHealthCheck.LivenessProbePath);
    }

    [Fact]
    public async Task Chat_upstream_error_is_Unhealthy()
    {
        await using var upstream = await StubChatServiceAsync(
            firebase: HttpStatusCode.InternalServerError, check: HttpStatusCode.OK);

        var result = await ProbeChatAsync(chatEnabled: true, upstream.Url);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("500");
    }

    [Fact]
    public async Task Chat_upstream_timeout_is_Unhealthy_and_never_throws()
    {
        // Nothing is listening on this port, so the client faults inside the budget.
        var result = await ProbeChatAsync(chatEnabled: true, "http://127.0.0.1:1");

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Chat_readiness_is_on_the_declared_roster()
    {
        GatewayHealthRoster.Ready.Should().Contain(ChatUpstreamHealthCheck.Name);
        GatewayHealthRoster.Ready.Should().HaveCount(GatewayHealthRoster.ExpectedReadyCount);
        GatewayHealthRoster.ExpectedReadyCount.Should().Be(27);
    }

    // --------------------------------------------------------- credentials

    [Fact]
    public void Every_declared_credential_has_a_ready_row()
    {
        foreach (var credential in GatewayCredentialDeclarations.All)
        {
            GatewayHealthRoster.Ready.Should().Contain(credential.Name);
        }
    }

    [Fact]
    public void No_effective_production_configuration_defaults_a_credential_to_a_host_path()
    {
        // The 608debf class: a Swarm-only /run/secrets default on a native host. Assert the
        // MERGED base+Production configuration, so Production cannot inherit a base default.
        var root = Path.Combine(RepositoryRoot(), "src", "JeebGateway");
        var production = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "appsettings.json"))
            .AddJsonFile(Path.Combine(root, "appsettings.Production.json"))
            .Build();

        foreach (var credential in GatewayCredentialDeclarations.All)
        {
            foreach (var source in credential.Chain.Where(
                s => s.Kind == GatewayCredentialSourceKind.SecretFile))
            {
                production[source.ConfigurationKey].Should().BeNullOrEmpty(
                    $"{source.ConfigurationKey} must be supplied by the deploy, never defaulted in code");
            }
        }

        File.ReadAllLines(Path.Combine(root, "appsettings.Production.json"))
            .Where(line => !line.TrimStart().StartsWith("\"_comment", StringComparison.Ordinal))
            .Should().NotContain(line => line.Contains("/run/secrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Credential_resolved_from_an_environment_value_is_Healthy()
    {
        var result = await ProbeCredentialAsync(
            "credential-delivery-service-token",
            ("FeatureFlags:UseUpstream:Delivery", "true"),
            ("DELIVERY_SERVICE_TOKEN", "a-delivery-service-token"));

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("DELIVERY_SERVICE_TOKEN");
    }

    [Fact]
    public async Task Credential_resolved_from_a_mounted_file_is_Healthy()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        await File.WriteAllTextAsync(file, "a-mounted-state-service-token");
        try
        {
            var result = await ProbeCredentialAsync(
                "credential-state-service-token",
                ("JeebStateService:Enabled", "true"),
                ("JeebStateService:ServiceTokenFile", file));

            result.Status.Should().Be(HealthStatus.Healthy);
            result.Description.Should().Contain("JeebStateService:ServiceTokenFile");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Credential_configured_but_unresolvable_is_Unhealthy_with_an_actionable_description()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var result = await ProbeCredentialAsync(
            "credential-state-service-token",
            ("JeebStateService:Enabled", "true"),
            ("JeebStateService:ServiceTokenFile", missing));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should()
            .Contain(missing)
            .And.Contain("does not exist on this host")
            .And.Contain("Resolution chain");
    }

    [Fact]
    public async Task Credential_armed_with_no_source_at_all_is_Degraded_naming_the_chain()
    {
        var result = await ProbeCredentialAsync(
            "credential-bundler-cms-bearer",
            ("BUNDLER_CMS_BASE_URL", "http://127.0.0.1:10056/"));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should()
            .Contain("no source configured")
            .And.Contain("BUNDLER_CMS_BEARER_TOKEN_FILE");
    }

    [Fact]
    public async Task Credential_that_is_not_armed_is_Healthy()
    {
        var result = await ProbeCredentialAsync("credential-delivery-service-token");

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("not armed");
    }

    // ------------------------------------------------------------ helpers

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static async Task<HealthCheckResult> ProbeCredentialAsync(
        string name,
        params (string Key, string Value)[] values)
    {
        var declaration = GatewayCredentialDeclarations.All.Single(d => d.Name == name);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();
        return await new ConfiguredCredentialHealthCheck(declaration, configuration)
            .CheckHealthAsync(new HealthCheckContext());
    }

    private static async Task<HealthCheckResult> ProbeChatAsync(bool chatEnabled, string? upstream)
    {
        var settings = new Dictionary<string, string?>
        {
            [ChatUpstreamHealthCheck.FlagConfigurationKey] = chatEnabled ? "true" : "false",
        };
        if (upstream is not null)
        {
            settings[ChatUpstreamHealthCheck.BaseUrlConfigurationKey] = upstream;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddHttpClient(ChatUpstreamHealthCheck.HttpClientName, client =>
        {
            if (upstream is not null)
            {
                client.BaseAddress = new Uri(upstream.EndsWith('/') ? upstream : upstream + "/");
            }

            client.Timeout = ChatUpstreamHealthCheck.Budget;
        });

        await using var provider = services.BuildServiceProvider();
        var check = new ChatUpstreamHealthCheck(
            configuration, provider.GetRequiredService<IHttpClientFactory>());
        return await check.CheckHealthAsync(new HealthCheckContext());
    }

    private static async Task<StubChatService> StubChatServiceAsync(
        HttpStatusCode firebase,
        HttpStatusCode check)
    {
        var hits = new ConcurrentBag<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/" + ChatUpstreamHealthCheck.FirestoreProbePath, (HttpContext ctx) =>
        {
            hits.Add(ctx.Request.Path.Value!);
            return Results.StatusCode((int)firebase);
        });
        app.MapGet("/" + ChatUpstreamHealthCheck.LivenessProbePath, (HttpContext ctx) =>
        {
            hits.Add(ctx.Request.Path.Value!);
            return Results.StatusCode((int)check);
        });
        await app.StartAsync();
        return new StubChatService(app, hits);
    }

    private sealed class StubChatService(WebApplication app, ConcurrentBag<string> hits)
        : IAsyncDisposable
    {
        public string Url { get; } = app.Urls.First();

        public ConcurrentBag<string> Hits { get; } = hits;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
