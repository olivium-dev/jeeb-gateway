using System.Net;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

// Regression coverage for the gateway -> push-notification topic seam: the typed client must read
// PushNotificationServiceApi, not the obsolete Services key that leaves it on its fallback.
[UnsupportedOSPlatform("windows")]
public sealed class PushNotificationTopicDeploymentContractTests
{
    private const string ConfiguredBaseUrl = "https://configured-push.test/internal-root";
    private const string ConfiguredApiKey = "integration-only-push-key";

    [Fact]
    public async Task RegisteredTopicClient_UsesConfiguredBaseUrl_AndSendsInternalApiKey()
    {
        var handler = new CapturingHandler();
        using var outbound = new HttpClient(handler);
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("PushNotificationServiceApi:BaseUrl", ConfiguredBaseUrl);
                builder.UseSetting("PushNotificationServiceApi:InternalApiKey", ConfiguredApiKey);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(outbound));
                });
            });

        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ServicePushNotificationClient>();

        var response = await client.Send_notification_to_topicAsync(
            "jeeb_jeebers",
            new SentPayloadToTopicRequest { Payload = new { type = "new_request" } },
            CancellationToken.None);

        response.Message.Should().Be("queued");
        handler.Method.Should().Be(HttpMethod.Post);
        handler.RequestUri.Should().Be(
            new Uri($"{ConfiguredBaseUrl}/api/v1/sent-payload/topic/jeeb_jeebers"));
        handler.ApiKey.Should().Be(ConfiguredApiKey);
    }

    public static IEnumerable<object[]> InvalidLifecycleInvocations() =>
    [
        ["unknown"],
        ["gc jeeb-gateway;docker-service-rm"],
        ["stabilize jeeb-gateway jeeb_gateway_appsettings_latest"],
        ["stabilize other-service jeeb_gateway_appsettings_12_1"],
        ["finalize 2 jeeb-gateway jeeb_gateway_appsettings_12_1 none"],
    ];

    [Theory]
    [MemberData(nameof(InvalidLifecycleInvocations))]
    public async Task SecretLifecycle_RejectsAdversarialIdentifiersBeforeDocker(string arguments)
    {
        var repoRoot = LocateRepoRoot();
        var script = Path.Combine(repoRoot, ".github", "scripts", "jeeb-gateway-secret-lifecycle.sh");
        var startInfo = new ProcessStartInfo("/bin/bash", $"{script} {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();

        process.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public void SecretLifecycle_PausesFailedUpdatesAndHasNoExecutableRollbackPath()
    {
        var repoRoot = LocateRepoRoot();
        var script = File.ReadAllText(Path.Combine(
            repoRoot,
            ".github",
            "scripts",
            "jeeb-gateway-secret-lifecycle.sh"));

        script.Should().Contain("--update-failure-action pause");
        script.Should().Contain("forbidden automatic rollback state detected");
        script.Should().Contain("assert_exact_running_image \"$service_name\" \"$expected_image\"");
        script.Should().Contain("service image changed during restart");
        script.Should().Contain("running task image ID changed during restart");
        script.Should().NotContain("--update-failure-action rollback");
        script.Should().NotContain("--rollback-order");
        script.Should().NotContain("docker service rollback");
        script.Should().NotContain("recover_existing");
        script.Should().NotContain("previous_image");
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".github", "workflows", "deploy-to-jeeb.yml"))
                && File.Exists(Path.Combine(directory.FullName, "src", "JeebGateway", "Program.cs")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the jeeb-gateway repository root.");
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("X-Api-Key", out var values)
                ? values.SingleOrDefault()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"message":"queued","timestamp":"2026-07-18T00:00:00Z"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
