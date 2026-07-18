using System.Net;
using System.Diagnostics;
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

/// <summary>
/// Regression coverage for the gateway -> push-notification topic seam. The runtime
/// client and the Swarm workflow must use the same <c>PushNotificationServiceApi</c>
/// configuration section; a syntactically valid but obsolete <c>Services</c> key leaves
/// the typed client pointed at its committed fallback and silently drops internal auth.
/// </summary>
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

    [Fact]
    public void DeployAndCi_UseTheConfigurationKeysConsumedByTheTypedClient()
    {
        var repoRoot = LocateRepoRoot();
        var deploy = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-to-jeeb.yml"));
        var ci = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));
        deploy.Should().Contain("PUSH_BASE_URL: ${{ inputs.push_notification_base_url }}");
        deploy.Should().Contain(
            "[[ \"$PUSH_BASE_URL\" == http://192.168.2.50:10040 ]]",
            "the DEV dispatch must reject an operator-supplied upstream or shell fragment");
        deploy.Should().Contain("\"PushNotificationServiceApi__BaseUrl=$PUSH_URL\"");
        deploy.Should().Contain("--rawfile push_key");
        deploy.Should().Contain("DB_HOST: ${{ secrets.JEEB_DB_HOST }}");
        deploy.Should().Contain("DB_PORT: ${{ secrets.JEEB_DB_PORT }}");
        deploy.Should().Contain("DB_USERNAME: ${{ secrets.JEEB_DB_USERNAME }}");
        deploy.Should().Contain("DB_PASSWORD: ${{ secrets.JEEB_DB_PASSWORD }}");
        deploy.Should().Contain("DEPLOY_DB_NAME: jeeb");
        deploy.Should().NotContain("secrets.JEEB_DATABASE_URL",
            "the workflow must form the DEV connection string from the existing component secrets");
        deploy.Should().NotContain("psql \"$DATABASE_URL\"");
        deploy.Should().Contain("PushNotificationServiceApi: {InternalApiKey: $push_key}");
        deploy.Should().Contain("target=$SECRET_TARGET,uid=$APP_UID,gid=$APP_GID,mode=0400");
        deploy.Should().Contain("APPSETTINGS_SECRET_TARGET: /app/appsettings.Production.json");
        deploy.Should().NotContain("--env-add PushNotificationServiceApi__InternalApiKey=");
        deploy.Should().NotContain("--env PushNotificationServiceApi__InternalApiKey=");

        deploy.Should().NotContain("Services__PushNotification__BaseUrl",
            "the obsolete namespace is ignored by the typed topic client");
        ci.Should().Contain(
            "PushNotificationServiceApi__BaseUrl=\"http://push-notification:8080\"");
        ci.Should().NotContain("Services__PushNotification__BaseUrl",
            "the image smoke must exercise the production configuration contract");
    }

    [Fact]
    public void DeployWorkflow_HasImmutableIdentityPinnedSshAndConvergentRecovery()
    {
        var repoRoot = LocateRepoRoot();
        var deploy = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-to-jeeb.yml"));
        var lifecycle = File.ReadAllText(Path.Combine(repoRoot, ".github", "scripts", "jeeb-gateway-secret-lifecycle.sh"));

        deploy.Should().Contain("TAG=\"$IMAGE:sha-${GITHUB_SHA::12}\"");
        deploy.Should().Contain("docker buildx imagetools inspect \"$TAG\"");
        deploy.Should().Contain("IMAGE_REF=\"$IMAGE@$DIGEST\"");
        deploy.Should().Contain("StrictHostKeyChecking yes");
        deploy.Should().Contain("KNOWN_HOSTS_MATERIAL: ${{ secrets.JEEB_SSH_KNOWN_HOSTS }}");
        deploy.Should().Contain("--update-order start-first --update-failure-action rollback");
        deploy.Should().Contain("mode=ingress");
        deploy.Should().Contain("Recover failed rollout to captured digest");
        lifecycle.Should().Contain("docker service update --image \"$previous_image\"");
        lifecycle.Should().Contain("explicit rollback did not restore the captured digest");
        lifecycle.Should().Contain("secret_is_referenced \"$candidate\"");
    }

    public static IEnumerable<object[]> ForbiddenDeployFragments() =>
    [
        ["StrictHostKeyChecking accept-new"],
        ["StrictHostKeyChecking no"],
        ["${IMAGE}:latest"],
        ["docker push \"${IMAGE}:latest\""],
        ["docker service rm \"$SVC\""],
        ["psql \"$DATABASE_URL\""],
        ["--env-add Security__TokenMint__Key="],
        ["--env-add JeebJwt__SigningKey="],
        ["--env-add UmJwt__SigningKey="],
        ["--env-add Whisper__ApiKey="],
    ];

    [Theory]
    [MemberData(nameof(ForbiddenDeployFragments))]
    public void DeployWorkflow_RejectsLegacyOrCredentialBearingPatterns(string fragment)
    {
        var repoRoot = LocateRepoRoot();
        var deploy = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-to-jeeb.yml"));

        deploy.Should().NotContain(fragment);
    }

    public static IEnumerable<object[]> InvalidLifecycleInvocations() =>
    [
        ["unknown"],
        ["gc jeeb-gateway;docker-service-rm"],
        ["stabilize jeeb-gateway jeeb_gateway_appsettings_latest"],
        ["stabilize other-service jeeb_gateway_appsettings_12_1"],
        ["finalize 2 jeeb-gateway jeeb_gateway_appsettings_12_1 none none"],
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
