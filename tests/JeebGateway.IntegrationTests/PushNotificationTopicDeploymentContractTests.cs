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

/// <summary>
/// Regression coverage for the gateway -> push-notification topic seam. The runtime
/// client and the Swarm workflow must use the same <c>PushNotificationServiceApi</c>
/// configuration section; a syntactically valid but obsolete <c>Services</c> key leaves
/// the typed client pointed at its committed fallback and silently drops internal auth.
/// </summary>
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

    [Fact]
    public void GatewayOnlyExposurePreflight_PrecedesEveryRemoteMutation()
    {
        var repoRoot = LocateRepoRoot();
        var deploy = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-to-jeeb.yml"));

        var preflight = deploy.IndexOf(
            "- name: Preflight gateway-only backend exposure (read-only)",
            StringComparison.Ordinal);
        var pinnedSsh = deploy.IndexOf(
            "- name: Install cloudflared and configure pinned SSH identity",
            StringComparison.Ordinal);
        var priorDigest = deploy.IndexOf(
            "- name: Capture strict rollback digest",
            StringComparison.Ordinal);
        var registryBuild = deploy.IndexOf(
            "- name: Build, push, and verify immutable image digest",
            StringComparison.Ordinal);
        var remoteRegistryLogin = deploy.IndexOf(
            "- name: Remote GHCR login after read-only preflights",
            StringComparison.Ordinal);
        var migration = deploy.IndexOf(
            "- name: Apply idempotent migrations without credential argv",
            StringComparison.Ordinal);
        var secretCreate = deploy.IndexOf(
            "ssh jeeb docker secret create \"$NEW_SECRET\" -",
            StringComparison.Ordinal);
        var serviceUpdate = deploy.IndexOf(
            "docker service update --image \"$IMAGE_REF\"",
            StringComparison.Ordinal);
        var serviceCreate = deploy.IndexOf(
            "docker service create --name \"$SVC\"",
            StringComparison.Ordinal);

        preflight.Should().BeGreaterThanOrEqualTo(0);
        pinnedSsh.Should().BeLessThan(preflight);
        preflight.Should().BeLessThan(priorDigest);
        preflight.Should().BeLessThan(registryBuild);
        preflight.Should().BeLessThan(remoteRegistryLogin);
        preflight.Should().BeLessThan(migration);
        preflight.Should().BeLessThan(secretCreate);
        preflight.Should().BeLessThan(serviceUpdate);
        preflight.Should().BeLessThan(serviceCreate);
        priorDigest.Should().BeLessThan(registryBuild);
        priorDigest.Should().BeLessThan(remoteRegistryLogin);
        priorDigest.Should().BeLessThan(migration);
        priorDigest.Should().BeLessThan(secretCreate);
        priorDigest.Should().BeLessThan(serviceUpdate);
        priorDigest.Should().BeLessThan(serviceCreate);
        deploy.Should().Contain(
            "expected=\"-A DOCKER-USER -i $public_if -p tcp -m conntrack --ctorigdstport $port -j DROP\"");
        deploy.Should().Contain("\"http://${public_ip}:${PRIVATE_PUSH_PORT}/health\"");
        deploy.LastIndexOf(
            "- name: Verify gateway is the sole public ingress",
            StringComparison.Ordinal).Should().BeGreaterThan(serviceUpdate,
                "the exposure policy must also be rechecked after rollout");
    }

    [Fact]
    public void DeployWorkflow_IsLockedToTheExpectedRepositoryBranchAndCommit()
    {
        var repoRoot = LocateRepoRoot();
        var deploy = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "deploy-to-jeeb.yml"));

        deploy.Should().Contain(
            "expected_commit: { description: 'Exact 40-hex commit that this DEV run must deploy', required: true }");
        deploy.Should().Contain("EXPECTED_REPOSITORY: olivium-dev/jeeb-gateway");
        deploy.Should().Contain("EXPECTED_REF: refs/heads/codex/fix-jeeber-push-e2e-20260718");
        deploy.Should().Contain("[[ \"$GITHUB_REPOSITORY\" == \"$EXPECTED_REPOSITORY\" ]]");
        deploy.Should().Contain("[[ \"$GITHUB_REF\" == \"$EXPECTED_REF\" ]]");
        deploy.Should().Contain("[[ \"$EXPECTED_COMMIT\" =~ ^[0-9a-f]{40}$ ]]");
        deploy.Should().Contain("[[ \"$EXPECTED_COMMIT\" == \"$GITHUB_SHA\" ]]");
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

    public static IEnumerable<object[]> InvalidRollbackCaptureStates() =>
    [
        ["transport-failure", "present"],
        ["pass", "permission-failure"],
        ["pass", "manager-failure"],
        ["pass", "inspect-failure-existing"],
        ["pass", "empty-success"],
        ["pass", "malformed-success"],
    ];

    [Theory]
    [MemberData(nameof(InvalidRollbackCaptureStates))]
    public async Task RollbackCapture_FailsClosedWithoutFirstCreateSemantics(
        string sshMode,
        string dockerMode)
    {
        var result = await RunRollbackCaptureAsync(sshMode, dockerMode);

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().NotContain("service_existed=0");
        result.Output.Should().NotContain("image=none");
    }

    [Theory]
    [InlineData("present", "service_existed=1")]
    [InlineData("absent", "service_existed=0")]
    public async Task RollbackCapture_EmitsExistenceOnlyForProvenStates(
        string dockerMode,
        string expectedOutput)
    {
        var result = await RunRollbackCaptureAsync("pass", dockerMode);

        result.ExitCode.Should().Be(0, result.StandardError);
        result.Output.Should().Contain(expectedOutput);
    }

    private static async Task<CaptureResult> RunRollbackCaptureAsync(
        string sshMode,
        string dockerMode)
    {
        var temp = Directory.CreateTempSubdirectory("jeeb-gateway-capture-");
        try
        {
            var fakeBin = Directory.CreateDirectory(Path.Combine(temp.FullName, "bin"));
            WriteExecutable(Path.Combine(fakeBin.FullName, "ssh"), FakeSsh);
            WriteExecutable(Path.Combine(fakeBin.FullName, "docker"), FakeDocker);
            var outputPath = Path.Combine(temp.FullName, "github-output");
            var script = ExtractWorkflowStepScript("Capture strict rollback digest");
            var startInfo = BuildCaptureStartInfo(
                script, fakeBin.FullName, outputPath, sshMode, dockerMode);

            using var process = Process.Start(startInfo)!;
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = File.Exists(outputPath)
                ? await File.ReadAllTextAsync(outputPath)
                : string.Empty;
            return new CaptureResult(process.ExitCode, output, standardError);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static ProcessStartInfo BuildCaptureStartInfo(
        string script,
        string fakeBin,
        string outputPath,
        string sshMode,
        string dockerMode)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["DEPLOY_SERVICE_NAME"] = "jeeb-gateway";
        startInfo.Environment["GITHUB_OUTPUT"] = outputPath;
        startInfo.Environment["FAKE_SSH_MODE"] = sshMode;
        startInfo.Environment["FAKE_DOCKER_MODE"] = dockerMode;
        startInfo.Environment["PATH"] = $"{fakeBin}:{startInfo.Environment["PATH"]}";
        return startInfo;
    }

    private static string ExtractWorkflowStepScript(string stepName)
    {
        var workflow = File.ReadAllText(Path.Combine(
            LocateRepoRoot(), ".github", "workflows", "deploy-to-jeeb.yml"));
        var stepStart = workflow.IndexOf($"      - name: {stepName}\n", StringComparison.Ordinal);
        stepStart.Should().BeGreaterThanOrEqualTo(0);
        var runStart = workflow.IndexOf("        run: |\n", stepStart, StringComparison.Ordinal);
        runStart.Should().BeGreaterThan(stepStart);
        var bodyStart = runStart + "        run: |\n".Length;
        var nextStep = workflow.IndexOf("\n      - ", bodyStart, StringComparison.Ordinal);
        var body = workflow[bodyStart..(nextStep < 0 ? workflow.Length : nextStep)];
        return string.Join('\n', body.Split('\n').Select(line =>
            line.StartsWith("          ", StringComparison.Ordinal) ? line[10..] : line));
    }

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed record CaptureResult(int ExitCode, string Output, string StandardError);

    private const string FakeSsh = """
        #!/usr/bin/env bash
        set -euo pipefail
        [[ "$FAKE_SSH_MODE" != transport-failure ]] || exit 255
        [[ "${1:-}" == jeeb ]] || exit 64
        shift
        exec "$@"
        """;

    private const string FakeDocker = """
        #!/usr/bin/env bash
        set -euo pipefail
        if [[ "${1:-}" == service && "${2:-}" == inspect ]]; then
          case "$FAKE_DOCKER_MODE" in
            present) printf '%s\n' 'ghcr.io/olivium-dev/jeeb-gateway:sha-aaaaaaaaaaaa@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' ;;
            empty-success) exit 0 ;;
            malformed-success) printf '%s\n' 'ghcr.io/olivium-dev/jeeb-gateway:latest' ;;
            absent|permission-failure|manager-failure|inspect-failure-existing) exit 1 ;;
            *) exit 64 ;;
          esac
          exit 0
        fi
        if [[ "${1:-}" == service && "${2:-}" == ls ]]; then
          case "$FAKE_DOCKER_MODE" in
            absent) printf '%s\n' other-service ;;
            inspect-failure-existing) printf '%s\n' jeeb-gateway ;;
            permission-failure|manager-failure) exit 1 ;;
            *) exit 64 ;;
          esac
          exit 0
        fi
        exit 64
        """;

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
