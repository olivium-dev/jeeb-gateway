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
    public void DeployAndCi_InjectTheConfigurationKeysConsumedByTheTypedClient()
    {
        var repoRoot = LocateRepoRoot();
        var deploy = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-to-jeeb.yml"));
        var ci = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci.yml"));
        var updateBranch = Slice(deploy, "            docker service update --image", "          else");
        var createBranch = Slice(deploy, "            docker service create --name", "          fi");

        deploy.Should().Contain("PUSH_BASE_URL: ${{ inputs.push_notification_base_url }}",
            "GitHub must bind the dispatch input through the step environment");
        deploy.Should().Contain("[[ ! \"$PUSH_BASE_URL\" =~ ^https?://([^/:?#]+)(:([0-9]{1,5}))?$ ]]",
            "the runner must reject anything beyond a root HTTP(S) origin");
        deploy.Should().Contain("printf -v PUSH_BASE_URL_Q '%q' \"$PUSH_BASE_URL\"",
            "the validated URL must be shell-escaped before crossing SSH");
        updateBranch.Should().Contain(
            "--env-add PushNotificationServiceApi__BaseUrl=$PUSH_BASE_URL_Q",
            "an update must refresh the exact BaseUrl section consumed by Program.cs");
        createBranch.Should().Contain(
            "--env PushNotificationServiceApi__BaseUrl=$PUSH_BASE_URL_Q",
            "a first create must seed the exact BaseUrl section consumed by Program.cs");
        updateBranch.Should().NotContain("inputs.push_notification_base_url",
            "raw workflow input expressions must not enter the remote heredoc");
        createBranch.Should().NotContain("inputs.push_notification_base_url",
            "raw workflow input expressions must not enter the remote heredoc");

        deploy.Should().Contain("PUSH_INTERNAL_API_KEY: ${{ secrets.JEEB_PUSH_INTERNAL_API_KEY }}");
        deploy.Should().Contain("[ -z \"${PUSH_INTERNAL_API_KEY:-}\" ]",
            "the required secret must fail closed before any remote mutation");
        deploy.Should().Contain("printf -v PUSH_INTERNAL_API_KEY_Q '%q' \"$PUSH_INTERNAL_API_KEY\"",
            "the secret must be shell-escaped without being printed before crossing SSH");
        updateBranch.Should().Contain(
            "--env-add PushNotificationServiceApi__InternalApiKey=$PUSH_INTERNAL_API_KEY_Q");
        createBranch.Should().Contain(
            "--env PushNotificationServiceApi__InternalApiKey=$PUSH_INTERNAL_API_KEY_Q");

        deploy.Should().NotContain("Services__PushNotification__BaseUrl",
            "the obsolete namespace is ignored by the typed topic client");
        ci.Should().Contain(
            "PushNotificationServiceApi__BaseUrl=\"http://push-notification:8080\"");
        ci.Should().NotContain("Services__PushNotification__BaseUrl",
            "the image smoke must exercise the production configuration contract");
    }

    public static IEnumerable<object[]> ValidPushBaseUrls() =>
    [
        ["http://192.168.2.50:10040"],
        ["http://push-notification:8080"],
        ["https://push.internal.example"],
        ["https://localhost:443"],
    ];

    [Theory]
    [MemberData(nameof(ValidPushBaseUrls))]
    public async Task PushBaseUrlValidation_TransportsAllowedOriginsAsOneOpaqueArgument(string input)
    {
        var result = await RunPushBaseUrlGuardAsync(input);

        result.ExitCode.Should().Be(0, result.StandardError);
        result.StandardOutput.Should().Be($"PushNotificationServiceApi__BaseUrl={input}");
    }

    public static IEnumerable<object[]> AdversarialPushBaseUrls() =>
    [
        ["ftp://push.internal:10040"],
        ["http://user@push.internal:10040"],
        ["http://push.internal:10040/path"],
        ["http://push.internal:10040?key=value"],
        ["http://push.internal:10040#fragment"],
        ["http://push.internal:0"],
        ["http://push.internal:65536"],
        ["http://-push.internal:10040"],
        ["http://push..internal:10040"],
        ["http://push.internal:10040\n--env-add PWNED=true"],
        ["http://push.internal:10040;docker service rm jeeb-gateway"],
    ];

    [Theory]
    [MemberData(nameof(AdversarialPushBaseUrls))]
    public async Task PushBaseUrlValidation_RejectsInjectionAndNonOriginInputs(string input)
    {
        var result = await RunPushBaseUrlGuardAsync(input);

        result.ExitCode.Should().NotBe(0);
        result.StandardOutput.Should().BeEmpty("rejected input must never reach the rendered docker argument");
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

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return source[start..end];
    }

    private static async Task<ProcessResult> RunPushBaseUrlGuardAsync(string input)
    {
        const string script = """
            set -euo pipefail
            if [[ "$PUSH_BASE_URL" == *$'\n'* || "$PUSH_BASE_URL" == *$'\r'* ]]; then exit 64; fi
            if [[ ! "$PUSH_BASE_URL" =~ ^https?://([^/:?#]+)(:([0-9]{1,5}))?$ ]]; then exit 64; fi
            PUSH_HOST="${BASH_REMATCH[1]}"
            PUSH_PORT="${BASH_REMATCH[3]:-}"
            if [ "${#PUSH_HOST}" -gt 253 ]; then exit 64; fi
            IFS='.' read -ra PUSH_HOST_LABELS <<< "$PUSH_HOST"
            for PUSH_HOST_LABEL in "${PUSH_HOST_LABELS[@]}"; do
              if [[ ! "$PUSH_HOST_LABEL" =~ ^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$ ]]; then exit 64; fi
            done
            if [ -n "$PUSH_PORT" ] && (( 10#$PUSH_PORT < 1 || 10#$PUSH_PORT > 65535 )); then exit 64; fi
            printf -v PUSH_BASE_URL_Q '%q' "$PUSH_BASE_URL"
            bash -se <<EOF
            set -euo pipefail
            set -- --env-add PushNotificationServiceApi__BaseUrl=$PUSH_BASE_URL_Q
            test "\$#" -eq 2
            printf '%s' "\$2"
            EOF
            """;

        var startInfo = new ProcessStartInfo("/bin/bash", ["-c", script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["PUSH_BASE_URL"] = input;

        using var process = Process.Start(startInfo)!;
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
