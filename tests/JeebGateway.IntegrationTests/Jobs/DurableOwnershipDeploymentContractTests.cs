using FluentAssertions;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class DurableOwnershipDeploymentContractTests
{
    private static readonly (string Target, string Invocation)[] ProductionMountedCredentials =
    [
        ("jeeb_state_service_token", "add_rotated_secret jeeb_state_service_token \"\\$STATE_SECRET\""),
        ("delivery_service_token", "add_rotated_secret delivery_service_token \"\\$DELIVERY_SECRET\""),
        ("notification_service_token", "add_rotated_secret notification_service_token \"\\$NOTIFICATION_SECRET\""),
        ("bundler_cms_bearer_token", "add_rotated_secret bundler_cms_bearer_token \"\\$BUNDLER_SECRET\""),
        ("private_artifact_store_bearer_token", "add_rotated_secret private_artifact_store_bearer_token \"\\$ARTIFACT_SECRET\""),
        ("data_export_token_signing_key", "add_rotated_secret data_export_token_signing_key \"\\$EXPORT_SECRET\""),
        ("jeeb_gateway_job_token", "add_rotated_secret jeeb_gateway_job_token \"\\$JOB_SECRET\"")
    ];

    private static readonly (string Target, string Invocation)[] StagingMountedCredentials =
    [
        ("jeeb_state_service_token", "add_rotated_secret \"$state_secret_name\" jeeb_state_service_token"),
        ("notification_service_token", "add_rotated_secret \"$notification_secret_name\" notification_service_token"),
        ("settlement_service_token", "add_rotated_secret \"$settlement_secret_name\" settlement_service_token"),
        ("bundler_cms_bearer_token", "add_rotated_secret \"$bundler_secret_name\" bundler_cms_bearer_token"),
        ("jeeb_gateway_job_token", "add_rotated_secret \"$job_secret_name\" jeeb_gateway_job_token"),
        ("jeeb_gateway_jwt", "add_rotated_secret \"$jwt_secret_name\" jeeb_gateway_jwt"),
        ("jeeb_gateway_umjwt", "add_rotated_secret \"$umjwt_secret_name\" jeeb_gateway_umjwt"),
        ("realtime_guardian_secret", "add_rotated_secret \"$guardian_secret_name\" realtime_guardian_secret"),
        ("staging_wss_probe_mint_key", "add_rotated_secret \"$probe_secret_name\" staging_wss_probe_mint_key"),
        ("realtime_membership_ticket_key", "add_rotated_secret \"$membership_secret_name\" realtime_membership_ticket_key"),
        ("firebase_admin_json", "add_rotated_secret \"$firebase_secret_name\" firebase_admin_json")
    ];

    [Theory]
    [InlineData("deploy-to-jeeb.yml")]
    [InlineData("jeeb-staging-deploy.yml")]
    public void Gateway_deploys_mount_every_owner_credential_as_a_versioned_secret(
        string workflowName)
    {
        var workflow = Workflow(workflowName);

        var credentials = workflowName == "jeeb-staging-deploy.yml"
            ? StagingMountedCredentials
            : ProductionMountedCredentials;

        foreach (var (target, invocation) in credentials)
        {
            workflow.Should().Contain(target);
            CountOccurrences(workflow, invocation).Should().Be(1,
                $"{target} must be mounted exactly once through add_rotated_secret");
        }
        CountOccurrences(workflow, "add_rotated_secret ").Should().Be(credentials.Length,
            "every versioned secret mount must have an explicitly reviewed helper invocation");

        var helper = ShellFunction(workflow, "add_rotated_secret");
        CountOccurrences(helper, "--secret-add").Should().Be(1);
        CountOccurrences(workflow, "--secret-add").Should().Be(1,
            "raw secret mounts outside add_rotated_secret must be rejected");
        if (workflowName == "jeeb-staging-deploy.yml")
            helper.Should().Contain(
                "target=$target_name,uid=65532,gid=65532,mode=0400");
        else
            helper.Should().Contain(
                "source=\\$source,target=\\$target,uid=65532,gid=65532,mode=0400");

        workflow.Should().NotContain("mode=0444");

        workflow.Should().Contain("JeebStateService__ServiceTokenFile");
        workflow.Should().Contain("/run/secrets/jeeb_state_service_token");
        workflow.Should().Contain("BUNDLER_CMS_BEARER_TOKEN_FILE");
        workflow.Should().Contain("InternalJobAuth__TokenFile");
        workflow.Should().NotContain("JeebStateService__ServiceToken=");
        workflow.Should().NotContain("InternalJobAuth__Token=");

        if (workflowName == "jeeb-staging-deploy.yml")
        {
            workflow.Should().Contain("Users__DataExport__Enabled false");
            workflow.Should().Contain("remove_secret_target private_artifact_store_bearer_token");
            workflow.Should().Contain("remove_secret_target data_export_token_signing_key");
            workflow.Should().NotContain("secrets.PRIVATE_ARTIFACT_STORE_BEARER_TOKEN");
            workflow.Should().NotContain("secrets.DATA_EXPORT_TOKEN_SIGNING_KEY");
        }
        else
        {
            workflow.Should().Contain("PRIVATE_ARTIFACT_STORE_BEARER_TOKEN_FILE");
            workflow.Should().Contain("DATA_EXPORT_TOKEN_SIGNING_KEY_FILE");
        }
    }

    [Fact]
    public void Jeeb_staging_uses_private_state_dns_and_does_not_require_dead_delivery_auth()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");

        workflow.Should().Contain(
            "JeebStateService__BaseUrl http://jeeb-staging-jeeb-state-service:8080");
        workflow.Should().NotContain("JeebStateService__BaseUrl http://192.168.2.20:10073");
        workflow.Should().NotContain("secrets.DELIVERY_SERVICE_TOKEN");
        workflow.Should().NotContain("add_rotated_secret \"$delivery_secret_name\"");
        workflow.Should().Contain("remove_secret_target delivery_service_token");
    }

    [Fact]
    public void Jeeb_staging_uses_verified_owner_dns_on_the_shared_overlay()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");

        workflow.Should().Contain("network=jeeb-staging-net");
        workflow.Should().Contain(
            "WalletServiceApi__BaseUrl http://jeeb-staging-wallet-service:8080/");
        workflow.Should().Contain(
            "ServiceNotificationClient__BaseUrl http://jeeb-staging-notification:8000/");
        workflow.Should().Contain("stale_gateway_network=jeeb-net");
        workflow.Should().Contain(
            "network_update_args+=(--network-rm \"$stale_gateway_network\")");

        workflow.Should().NotContain("wallet_network");
        workflow.Should().NotContain("http://wallet-service:8080");
        workflow.Should().NotContain("jeeb-staging-notification-service");
        workflow.Should().NotContain("WalletServiceApi__BaseUrl http://192.168.2.20");
        workflow.Should().NotContain("ServiceNotificationClient__BaseUrl http://192.168.2.20");
        workflow.Should().NotContain("11026");
    }

    [Fact]
    public void Jeeb_staging_removes_the_retired_payment_gateway_destination()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");

        workflow.Should().Contain("UnifiedPaymentGateway__BaseUrl");
        workflow.Should().Contain("\"${retired_gateway_env[@]}\"");
        workflow.Should().Contain("env_remove_args+=(--env-rm \"$stale_env\")");
    }

    [Fact]
    public void Jeeb_staging_mounts_the_settlement_service_scope_and_uses_private_dns()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");

        workflow.Should().Contain("SETTLEMENT_SERVICE_TOKEN: ${{ secrets.SETTLEMENT_SERVICE_TOKEN }}");
        workflow.Should().Contain(
            "settlement_secret_name=\"jeeb_staging_gateway_settlement_token_${GITHUB_RUN_ID}_${GITHUB_RUN_ATTEMPT}\"");
        workflow.Should().Contain(
            "add_rotated_secret \"$settlement_secret_name\" settlement_service_token");
        workflow.Should().Contain(
            "Services__Settlement__BaseUrl http://jeeb-staging-settlement-service:8080");
        workflow.Should().Contain(
            "Services__Settlement__ApiTokenFile /run/secrets/settlement_service_token");
        workflow.Should().Contain("Services__Settlement__ApiToken");

        workflow.Should().NotContain(
            "add_env Services__Settlement__ApiToken ");
        workflow.Should().NotContain(
            "Services__Settlement__BaseUrl http://192.168.2.20");
        workflow.Should().NotContain(
            "Services__Settlement__BaseUrl http://127.0.0.1");
    }

    [Fact]
    public void Jeeb_staging_enables_real_otp_and_keeps_unavailable_compliment_dormant()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");

        workflow.Should().Contain(
            "add_env Services__ServiceOTP__BaseUrl http://192.168.2.20:10037");
        workflow.Should().Contain(
            "add_env ComplimentServiceApi__BaseUrl http://192.168.2.20:10036");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Otp true");
        workflow.Should().Contain(
            "add_env Auth__Otp__ApplicationId 0d51afe1-499f-4a29-a55a-36d2dd223b05");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Compliment false");
        workflow.Should().Contain("add_env CatalogServiceApi__BaseUrl ''");

        workflow.Should().NotContain(
            "add_env ServiceOTPApi__BaseUrl http://192.168.2.20:10037");
        workflow.Should().NotContain(
            "add_env Services__Compliment__BaseUrl http://192.168.2.20:10036");
        workflow.Should().Contain("ServiceOTPApi__BaseUrl");
        workflow.Should().Contain("Services__Compliment__BaseUrl");
    }

    [Fact]
    public void Jeeb_staging_workflow_is_gateway_only_and_provider_secret_minimal()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        var automaticRollback = "--update-failure-action " + "rollback";
        var rollbackOrder = "--" + "rollback-order stop-first";
        var openAiSecret = "secrets.OPENAI" + "_API_KEY";

        workflow.Should().Contain("[ \"$REPOSITORY\" = jeeb-gateway ]");
        workflow.Should().Contain("Selective gateway deploy requires the incumbent service");
        workflow.Should().Contain(automaticRollback);
        workflow.Should().Contain(rollbackOrder);
        workflow.Should().Contain("--update-order stop-first");
        workflow.Should().Contain(".jeeb-deploy/ghcr-");
        workflow.Should().NotContain("docker service create");
        workflow.Should().NotContain("WHISPER_FAKE_TRANSCRIBE 1");
        workflow.Should().NotContain("secrets.JEEB_RTC_DATABASE_URL");
        workflow.Should().NotContain("secrets.JEEB_DATABASE_URL");
        workflow.Should().NotContain(openAiSecret);
    }

    [Fact]
    public void Scheduled_executor_uses_loopback_exact_contract_and_never_places_token_in_ssh_argv()
    {
        var workflow = Workflow("durable-work-sweep.yml");

        workflow.Should().Contain("cron: \"*/5 * * * *\"");
        workflow.Should().Contain(
            "http://127.0.0.1:10000/internal/jobs/%s/sweep?limit=20");
        workflow.Should().Contain("for job in account-deletions data-exports");
        workflow.Should().Contain("X-Jeeb-Job-Token: %s");
        workflow.Should().Contain("printf '%s' \"$GATEWAY_JOB_TOKEN\" | ssh jeeb");
        workflow.Should().Contain("Verify loopback and public-ingress boundaries");
        workflow.Should().NotContain("ssh jeeb \"$GATEWAY_JOB_TOKEN");
        workflow.Should().NotContain("curl -H \"X-Jeeb-Job-Token: $GATEWAY_JOB_TOKEN");
    }

    [Fact]
    public void Staging_state_auth_smoke_requires_denied_anonymous_and_successful_authenticated_probes()
    {
        var workflow = Workflow("jeeb-staging-state-auth-smoke.yml");

        workflow.Should().Contain("case \"$anonymous\" in 401|403)");
        workflow.Should().Contain("case \"$authenticated\" in 2??) ;; *)");
        workflow.Should().NotContain("case \"$authenticated\" in 0|401|403)");

        // Keep the evidence status-only and prove both sides of the authorized restart.
        workflow.Should().Contain("awk '/  HTTP\\// { code=$2 } END { print code+0 }'");
        workflow.Should().Contain("remote_probe before_restart");
        workflow.Should().Contain("remote_probe after_restart");
        workflow.Should().Contain("docker service update --force --update-failure-action pause --detach=false");
        workflow.Should().Contain("scripts/verify-swarm-service-image.sh");
        workflow.Should().Contain("[ \"$before_image\" = \"$after_image\" ]");
        workflow.Should().Contain("[ \"$before_task_image\" = \"$before_image\" ]");
        workflow.Should().Contain("[ \"$after_task_image\" = \"$after_image\" ]");
        workflow.Should().Contain("[ \"$after_task_image_id\" = \"$before_task_image_id\" ]");
        workflow.Should().Contain("[ \"$before_task\" != \"$after_task\" ]");
        workflow.Should().Contain("service_replicas=1/1");
        workflow.Should().Contain("smoke_workflow_commit=%s");
        workflow.Should().Contain("runtime_image=%s");
        workflow.Should().NotContain("docker service " + "rollback");
    }

    [Theory]
    [InlineData("deploy-to-jeeb.yml")]
    [InlineData("jeeb-staging-deploy.yml")]
    public void Gateway_deploys_use_the_environment_specific_safe_rollout_and_verify_exact_image(
        string workflowName)
    {
        var workflow = Workflow(workflowName);
        var verifier = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "verify-swarm-service-image.sh"));

        workflow.Should().Contain("steps.immutable.outputs.image");
        workflow.Should().Contain("scripts/verify-swarm-service-image.sh");
        verifier.Should().Contain(".Spec.TaskTemplate.ContainerSpec.Image");
        verifier.Should().Contain(".UpdateStatus");
        verifier.Should().Contain(".Spec.Mode.Replicated.Replicas");
        verifier.Should().Contain("desired-state=running");
        verifier.Should().Contain(".Status.State");
        verifier.Should().Contain(".DesiredState");
        verifier.Should().Contain(".ServiceID");
        verifier.Should().Contain(".Status.ContainerStatus.ContainerID");
        verifier.Should().Contain("docker image inspect");
        verifier.Should().Contain("{{.Image}}");
        if (workflowName == "jeeb-staging-deploy.yml")
        {
            workflow.Should().Contain("--update-order stop-first");
            workflow.Should().Contain("--update-failure-action " + "rollback");
            workflow.Should().Contain("--" + "rollback-order stop-first");
            workflow.Should().Contain("docker service " + "rollback --detach=false");
            workflow.Should().Contain("Incumbent service image is not digest-pinned");
            workflow.Should().NotContain("--update-order start-first");
        }
        else
        {
            const string runtimeVerifier = "base64 -d | bash -s -- \"\\$SVC\" \"\\$REQUESTED_IMAGE\"";
            var arm = workflow.IndexOf("rollback_armed=true", StringComparison.Ordinal);
            var runtimeVerifierIndex = workflow.IndexOf(runtimeVerifier, StringComparison.Ordinal);
            arm.Should().BeGreaterThanOrEqualTo(0);
            CountOccurrences(workflow, runtimeVerifier).Should().Be(1);
            var firstDisarmAfterArm = workflow.IndexOf(
                "rollback_armed=false",
                arm + "rollback_armed=true".Length,
                StringComparison.Ordinal);
            arm.Should().BeLessThan(runtimeVerifierIndex);
            runtimeVerifierIndex.Should().BeLessThan(firstDisarmAfterArm,
                "runtime identity verification must finish before the first recovery disarm");
            workflow.Should().Contain("--update-order stop-first");
            workflow.Should().Contain("--update-failure-action " + "rollback");
            workflow.Should().Contain("--" + "rollback-order stop-first");
            workflow.Should().Contain("docker service " + "rollback --detach=false");
            workflow.Should().Contain("Incumbent service image is not digest-pinned");
            workflow.Should().NotContain("--update-order start-first");
        }
    }

    private static string ShellFunction(string workflow, string name)
    {
        var marker = $"{name}() {{";
        var start = workflow.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"Missing shell helper: {name}");

        var lineStart = workflow.LastIndexOf('\n', start) + 1;
        var indentation = workflow[lineStart..start];
        var endMarker = $"\n{indentation}}}";
        var end = workflow.IndexOf(endMarker, start + marker.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"Unterminated shell helper: {name}");

        return workflow[start..(end + endMarker.Length)];
    }

    private static int CountOccurrences(string value, string candidate) =>
        value.Split(candidate, StringSplitOptions.None).Length - 1;

    private static string Workflow(string name) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), ".github", "workflows", name));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".github", "workflows")))
            current = current.Parent;

        return current?.FullName
               ?? throw new DirectoryNotFoundException("Could not find the gateway repository root.");
    }
}
