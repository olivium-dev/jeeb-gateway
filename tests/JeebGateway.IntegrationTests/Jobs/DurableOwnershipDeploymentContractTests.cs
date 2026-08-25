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
        if (workflowName == "jeeb-staging-deploy.yml")
        {
            CountOccurrences(helper, "secret_additions+=(").Should().Be(1);
            CountOccurrences(workflow, "secret_additions+=(").Should().Be(1,
                "raw candidate-Spec secret mounts outside add_rotated_secret must be rejected");
            helper.Should().Contain(
                "File:{Name:$target,UID:\"65532\",GID:\"65532\",Mode:256}");
        }
        else
        {
            CountOccurrences(helper, "--secret-add").Should().Be(1);
            CountOccurrences(workflow, "--secret-add").Should().Be(1,
                "raw secret mounts outside add_rotated_secret must be rejected");
            helper.Should().Contain(
                "source=\\$source,target=\\$target,uid=65532,gid=65532,mode=0400");
        }

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
        workflow.Should().Contain(
            ".TaskTemplate.Networks = [{Target:$network_id}]");
        workflow.Should().Contain("select(.Options | has(\"encrypted\"))");
        workflow.Should().Contain(
            "select(.Options.encrypted == \"\" or .Options.encrypted == \"true\")");
        workflow.Should().Contain("verify_staging_overlay_and_dns");
        workflow.Should().Contain("docker exec \"$container_id\" getent hosts \"$dns_name\"");

        workflow.Should().NotContain("wallet_network");
        workflow.Should().NotContain("stale_gateway_network=jeeb-net");
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
        workflow.Should().Contain("env_remove_keys+=(\"$stale_env\")");
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
            "add_env Services__ServiceOTP__BaseUrl http://jeeb-staging-one-time-password:8080");
        workflow.Should().Contain(
            "add_env ServiceOTPApi__BaseUrl http://jeeb-staging-one-time-password:8080");
        workflow.Should().Contain(
            "add_env ComplimentServiceApi__BaseUrl http://192.168.2.20:10036");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Otp true");
        workflow.Should().Contain(
            "add_env Auth__Otp__ApplicationId 0d51afe1-499f-4a29-a55a-36d2dd223b05");
        workflow.Should().Contain("add_env Auth__Otp__Phone__AllowedRegion LB");
        workflow.Should().Contain("add_env Auth__Otp__Phone__EnforceRegion true");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Compliment false");
        workflow.Should().Contain("add_env CatalogServiceApi__BaseUrl ''");

        workflow.Should().NotContain(
            "add_env ServiceOTPApi__BaseUrl http://192.168.2.20:10037");
        workflow.Should().NotContain(
            "add_env Services__ServiceOTP__BaseUrl http://192.168.2.20:10037");
        workflow.Should().NotContain(
            "add_env Services__Compliment__BaseUrl http://192.168.2.20:10036");
        workflow.Should().Contain("ServiceOTPApi__BaseUrl");
        workflow.Should().Contain("Services__Compliment__BaseUrl");
    }

    [Fact]
    public void Jeeb_staging_workflow_is_gateway_only_provider_secret_minimal_and_owner_blocked()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        var openAiSecret = "secrets.OPENAI" + "_API_KEY";

        workflow.Should().Contain("Owner block - forward-only promotion pending");
        workflow.Should().Contain("::error::Forward-only promotion pending owner-approved failure handling");
        workflow.Should().Contain("[ \"$REPOSITORY\" = jeeb-gateway ]");
        workflow.Should().Contain("GITHUB_REF_PROTECTED: ${{ github.ref_protected }}");
        workflow.Should().Contain("[ \"$GITHUB_REF_PROTECTED\" = true ]");
        workflow.Should().Contain("environment: staging");
        workflow.Should().Contain("[ \"$(hostname -s)\" = \"olivium-ephemerals\" ]");
        workflow.Should().Contain("grep -Fxc \"192.168.2.20\"");
        workflow.Should().Contain("Selective gateway deploy requires an incumbent service");
        workflow.Should().Contain("FailureAction:\"rollback\",Order:\"start-first\"");
        workflow.Should().Contain("FailureAction:\"pause\",Order:\"start-first\"");
        workflow.Should().Contain("staging_gateway_external_gate_recover");
        workflow.Should().Contain("staging_gateway_forward_apply");
        workflow.Should().Contain("registryAuthFrom=previous-spec");
        workflow.Should().Contain("${published}:${target}:ingress");
        workflow.Should().NotContain("${published}:${target}:host");
        workflow.Should().Contain(".jeeb-deploy/ghcr-");
        workflow.Should().NotContain("docker service create");
        workflow.Should().NotContain("docker service update --detach=false");
        workflow.Should().NotContain("WHISPER_FAKE_TRANSCRIBE 1");
        workflow.Should().NotContain("secrets.JEEB_RTC_DATABASE_URL");
        workflow.Should().NotContain("secrets.JEEB_DATABASE_URL");
        workflow.Should().NotContain(openAiSecret);
        workflow.IndexOf("Owner block - forward-only promotion pending", StringComparison.Ordinal)
            .Should().BeLessThan(workflow.IndexOf("docker/login-action@", StringComparison.Ordinal));
    }

    [Fact]
    public void Generic_production_deploy_rejects_the_exact_staging_target_before_external_mutation()
    {
        var workflow = Workflow("deploy-to-jeeb.yml");
        const string serviceGuard =
            "[ \"$REQUESTED_SERVICE\" = jeeb-staging-jeeb-gateway ]";
        const string hostGuard =
            "[ \"$REQUESTED_HOST\" = \"$STAGING_SSH_HOST\" ]";
        const string canonicalGuard =
            "bash scripts/reject-staging-gateway-alias.sh \"$REQUESTED_SERVICE\"";
        var aliasGuard = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "reject-staging-gateway-alias.sh"));

        workflow.Should().Contain("REQUESTED_SERVICE: ${{ inputs.service_name }}");
        workflow.Should().Contain("REQUESTED_HOST: ${{ inputs.server_hostname }}");
        workflow.Should().Contain("STAGING_SSH_HOST: ${{ secrets.JEEB_STAGING_SSH_HOST }}");
        CountOccurrences(workflow, serviceGuard).Should().Be(1);
        CountOccurrences(workflow, hostGuard).Should().Be(1);
        CountOccurrences(workflow, canonicalGuard).Should().Be(1);
        workflow.Should().Contain("*[!a-zA-Z0-9_.-]*)");
        aliasGuard.Should().Contain("if ! canonical_service=$(ssh jeeb");
        aliasGuard.Should().Contain("''|*$'\\n'*|*[!a-zA-Z0-9_.-]*)");
        aliasGuard.Should().Contain(
            "[ \"$canonical_service\" = jeeb-staging-jeeb-gateway ]");
        aliasGuard.Should().NotContain("2>/dev/null || true");
        workflow.Should().Contain("SSH_KNOWN_HOSTS: ${{ secrets.JEEB_SSH_KNOWN_HOSTS }}");
        workflow.Should().Contain("UserKnownHostsFile ~/.ssh/known_hosts");
        workflow.Should().Contain("StrictHostKeyChecking yes");
        workflow.Should().NotContain("StrictHostKeyChecking accept-new");
        workflow.Should().Contain("Owner block - forward-only promotion pending");
        workflow.Should().Contain("--update-failure-action pause");
        workflow.Should().NotContain("--update-failure-action " + "rollback");
        workflow.Should().NotContain("docker service " + "rollback");

        var firstExternalMutation = workflow.IndexOf("docker login", StringComparison.Ordinal);
        var ownerBlock = workflow.IndexOf(
            "Owner block - forward-only promotion pending", StringComparison.Ordinal);
        var sshSetup = workflow.IndexOf("Install cloudflared + write deploy key", StringComparison.Ordinal);
        var canonicalGuardIndex = workflow.IndexOf(canonicalGuard, StringComparison.Ordinal);
        var build = workflow.IndexOf("docker build", StringComparison.Ordinal);
        workflow.IndexOf(serviceGuard, StringComparison.Ordinal)
            .Should().BeLessThan(firstExternalMutation);
        workflow.IndexOf(hostGuard, StringComparison.Ordinal)
            .Should().BeLessThan(firstExternalMutation);
        ownerBlock.Should().BeLessThan(firstExternalMutation);
        sshSetup.Should().BeLessThan(canonicalGuardIndex);
        canonicalGuardIndex.Should().BeLessThan(firstExternalMutation,
            "Swarm IDs and aliases must be rejected before local registry mutation");
        firstExternalMutation.Should().BeLessThan(build);
    }

    [Fact]
    public void Jeeb_staging_rejects_every_signing_key_family_collision_before_staging_secrets()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        var contract = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "assert-distinct-staging-signing-keys.sh"));

        const string invocation = "bash scripts/assert-distinct-staging-signing-keys.sh";
        workflow.Should().Contain(invocation);
        foreach (var variable in new[]
                 {
                     "JWT_SIGNING_KEY",
                     "UMJWT_SIGNING_KEY",
                     "JEEB_RTC_GUARDIAN_SECRET_KEY",
                     "JEEB_RTC_MEMBERSHIP_TICKET_KEY",
                     "JEEB_STAGING_WSS_PROBE_MINT_KEY"
                 })
            contract.Should().Contain(variable);

        contract.Should().Contain("for ((left = 0; left < ${#key_values[@]}; left += 1))");
        contract.Should().Contain("for ((right = left + 1; right < ${#key_values[@]}; right += 1))");
        var preflight = workflow.IndexOf(invocation, StringComparison.Ordinal);
        var firstSecretName = workflow.IndexOf(
            "state_secret_name=\"jeeb_staging_gateway_",
            StringComparison.Ordinal);
        preflight.Should().BeLessThan(firstSecretName,
            "key-family collisions must fail before any secret name or object is staged");
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

        workflow.Should().Contain("Owner block - forward-only promotion pending");
        workflow.Should().Contain("::error::Forward-only promotion pending owner-approved failure handling");
        workflow.Should().Contain("if: ${{ success() }}");
        workflow.Should().NotContain("if: always()");
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
        workflow.Should().Contain("group: jeeb-staging-gateway-mutation");
        workflow.Should().Contain("source scripts/staging-gateway-mutation-lock.sh");
        workflow.Should().Contain("staging_gateway_lock_acquire");
        workflow.Should().Contain("staging_gateway_lock_assert");
        workflow.Should().Contain("staging_gateway_lock_release");
        workflow.IndexOf("Owner block - forward-only promotion pending", StringComparison.Ordinal)
            .Should().BeLessThan(workflow.IndexOf("actions/checkout@", StringComparison.Ordinal));
        workflow.IndexOf("staging_gateway_lock_assert", StringComparison.Ordinal)
            .Should().BeLessThan(
                workflow.IndexOf("docker service update --force", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("deploy-to-jeeb.yml")]
    [InlineData("jeeb-staging-deploy.yml")]
    public void Gateway_deploy_templates_are_owner_blocked_and_verify_exact_image(
        string workflowName)
    {
        var workflow = Workflow(workflowName);
        var verifier = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "verify-swarm-service-image.sh"));
        var automaticRollback = "--update-failure-action " + "rollback";
        var rollbackOption = "--" + "rollback-order";

        workflow.Should().Contain("steps.immutable.outputs.image");
        workflow.Should().Contain("scripts/verify-swarm-service-image.sh");
        workflow.Should().Contain("Owner block - forward-only promotion pending");
        workflow.Should().Contain("::error::Forward-only promotion pending owner-approved failure handling");
        workflow.IndexOf("Owner block - forward-only promotion pending", StringComparison.Ordinal)
            .Should().BeLessThan(workflow.IndexOf("actions/checkout@", StringComparison.Ordinal));
        workflow.Should().NotContain("continue-on-error:");
        workflow.Should().NotContain("if: ${{ always() }}");
        workflow.Should().NotContain("if: ${{ failure() }}");
        workflow.Should().NotContain("if: ${{ cancelled() }}");
        workflow.Should().NotContain("docker service " + "rollback");
        verifier.Should().Contain(".Spec.TaskTemplate.ContainerSpec.Image");
        verifier.Should().Contain(".UpdateStatus");
        verifier.Should().Contain("initial|completed) break");
        verifier.Should().Contain("updating) sleep 4");
        verifier.Should().Contain("initial|completed) ;;");
        verifier.Should().NotContain("rollback_" + "completed");
        verifier.Should().NotContain("rollback_" + "started");
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
            workflow.Should().Contain("FailureAction:\"rollback\",Order:\"start-first\"");
            workflow.Should().Contain("FailureAction:\"pause\",Order:\"start-first\"");
            workflow.Should().Contain("staging_gateway_external_gate_recover");
            workflow.Should().Contain("staging_gateway_forward_apply");
            workflow.Should().Contain("recovery_armed=true");
            workflow.Should().Contain("docker service inspect '$service' --format '{{json .Spec}}'");
            workflow.Should().Contain("docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'");
            workflow.Should().Contain("cmp -s \"$pre_update_spec\" \"$incumbent_spec\"");
            workflow.Should().Contain("cmp -s \"$pre_update_version\" \"$incumbent_version\"");
            workflow.Should().Contain("cmp -s \"$pre_update_id\" \"$incumbent_id\"");
            workflow.Should().Contain("verify_exact_candidate_after_checks");
            workflow.Should().Contain("EXPECTED_INCUMBENT_IMAGE");
            workflow.Should().Contain("EXPECTED_INCUMBENT_SPEC_SHA");
            workflow.Should().Contain("registryAuthFrom=previous-spec");
            workflow.Should().NotContain("docker service update --detach=false");
            workflow.Should().Contain("Incumbent service image is not digest-pinned");
        }
        else
        {
            workflow.Should().Contain("--update-order stop-first");
            workflow.Should().Contain("--update-failure-action pause");
            workflow.Should().NotContain(automaticRollback);
            workflow.Should().NotContain(rollbackOption);
            workflow.Should().NotContain("staging_gateway_external_gate_recover");
            workflow.Should().NotContain("--update-order start-first");
            const string runtimeVerifier = "base64 -d | bash -s -- \"\\$SVC\" \"\\$REQUESTED_IMAGE\"";
            CountOccurrences(workflow, runtimeVerifier).Should().Be(1);
            workflow.Should().Contain("Deployed service spec does not match the requested immutable digest");
        }
    }

    [Fact]
    public void Jeeb_staging_is_an_owner_blocked_non_activating_full_spec_template()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        workflow.Should().Contain("Owner block - forward-only promotion pending");
        workflow.Should().Contain("FailureAction:\"rollback\",Order:\"start-first\"");
        workflow.Should().Contain("FailureAction:\"pause\",Order:\"start-first\"");
        workflow.Should().NotContain("docker service " + "rollback");
        workflow.Should().NotContain("docker service update --detach=false");
        workflow.Should().Contain("staging_gateway_external_gate_recover");
        workflow.Should().Contain("staging_gateway_forward_apply");
        workflow.Should().Contain("recovery_armed=true");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Chat false");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Realtime false");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Voice false");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Otp true");
        workflow.Should().NotContain("add_env FeatureFlags__UseUpstream__Chat true");
        workflow.Should().NotContain("add_env FeatureFlags__UseUpstream__Realtime true");
        workflow.Should().NotContain("add_env FeatureFlags__UseUpstream__Voice true");
        workflow[..workflow.IndexOf("permissions:", StringComparison.Ordinal)]
            .Should().NotContain("inputs:");
        workflow.Should().NotContain("${{ inputs.");

        workflow.Should().Contain("capture_remote_spec() {");
        workflow.Should().Contain("write_snapshot_manifest() {");
        workflow.Should().Contain("ServiceID: $id");
        workflow.Should().Contain("VersionIndex: $version");
        workflow.Should().Contain("ImageDigest: $digest");
        workflow.Should().Contain("Ports: ($spec[0].EndpointSpec.Ports // [])");
        workflow.Should().Contain("Networks: ($spec[0].TaskTemplate.Networks // [])");
        workflow.Should().Contain("Replicas: $spec[0].Mode.Replicated.Replicas");
        workflow.Should().Contain("SecretNames: ([");
        workflow.Should().Contain("{{json .Spec}}");
        workflow.Should().Contain("{{.ID}} {{.Version.Index}}");
        workflow.Should().Contain("cmp -s \"$pre_update_spec\" \"$incumbent_spec\"");
        workflow.Should().Contain("cmp -s \"$pre_update_version\" \"$incumbent_version\"");
        workflow.Should().Contain("cmp -s \"$pre_update_id\" \"$incumbent_id\"");
        workflow.Should().Contain("tolower($1) == tolower(expected)");
        workflow.Should().Contain("matches == 1 && exact_value == 1");
        workflow.Should().Contain("verify_exact_candidate_after_checks() {");
        workflow.Should().Contain("cmp -s \"$final_spec\" \"$candidate_spec\"");
        workflow.Should().Contain("cmp -s \"$final_version\" \"$candidate_version\"");
        workflow.Should().Contain("cmp -s \"$final_id\" \"$candidate_id\"");
        workflow.Should().Contain("group: jeeb-staging-gateway-mutation");
        workflow.Should().Contain("source scripts/staging-gateway-mutation-lock.sh");
        workflow.Should().Contain("staging_gateway_lock_acquire");
        workflow.Should().Contain("staging_gateway_lock_assert");
        workflow.Should().Contain("staging_gateway_lock_release");
        workflow.Should().Contain(
            "add_env Operations__RealtimeProbe__MintKeyFile /run/secrets/staging_wss_probe_mint_key");
        workflow.Should().Contain(
            "add_env Services__Realtime__GuardianSecretFile /run/secrets/realtime_guardian_secret");
        workflow.Should().Contain(
            "add_env Services__Realtime__MembershipTicketSigningKeyFile /run/secrets/realtime_membership_ticket_key");
        workflow.Should().Contain(
            "add_env Services__Realtime__PublicSocketUrl wss://app.jeeb.fds-1.com/socket/websocket");
        workflow.Should().Contain(
            "add_env Services__Realtime__BaseUrl http://jeeb-staging-realtime-comunication-service:4000");
        workflow.Should().NotContain(
            "add_env Services__Realtime__BaseUrl http://192.168.2.20:10069");
        workflow.Should().Contain("python3 scripts/probe-staging-authenticated-realtime.py");
        workflow.Should().Contain("probe_staging_proxy_source_contract");
        CountOccurrences(workflow, "scripts/verify-swarm-service-image.sh").Should().Be(2,
            "the blocked template retains exact candidate and recovered-incumbent verifiers");

        var ownerBlock = workflow.IndexOf(
            "Owner block - forward-only promotion pending", StringComparison.Ordinal);
        var firstExternalMutation = workflow.IndexOf("docker/login-action@", StringComparison.Ordinal);
        var preUpdate = workflow.IndexOf(
            "capture_remote_spec \"$pre_update_spec\" \"$pre_update_version\" \"$pre_update_id\"",
            StringComparison.Ordinal);
        var candidate = workflow.IndexOf(
            "> \"$candidate_spec\"",
            preUpdate,
            StringComparison.Ordinal);
        var candidateSemantics = workflow.IndexOf(
            "-f scripts/staging-gateway-candidate-contract.jq",
            candidate,
            StringComparison.Ordinal);
        var arm = workflow.IndexOf(
            "recovery_armed=true",
            candidate,
            StringComparison.Ordinal);
        var forward = workflow.IndexOf(
            "staging_gateway_forward_apply \\",
            arm,
            StringComparison.Ordinal);
        var imageVerifier = workflow.IndexOf(
            "scripts/verify-swarm-service-image.sh",
            forward,
            StringComparison.Ordinal);
        var readiness = workflow.IndexOf(
            "verify_candidate_readiness",
            imageVerifier,
            StringComparison.Ordinal);
        var network = workflow.IndexOf(
            "verify_staging_overlay_and_dns",
            readiness,
            StringComparison.Ordinal);
        var flags = workflow.IndexOf("verify_bootstrap_flags", network, StringComparison.Ordinal);
        var publicProbe = workflow.IndexOf(
            "bash scripts/probe-staging-public-gateway-contract.sh",
            imageVerifier,
            StringComparison.Ordinal);
        var proxyProbe = workflow.IndexOf(
            "probe_staging_proxy_source_contract",
            publicProbe,
            StringComparison.Ordinal);
        var descriptor = workflow.IndexOf(
            "probe_staging_authenticated_realtime",
            proxyProbe,
            StringComparison.Ordinal);
        var finalCandidate = workflow.IndexOf(
            "verify_exact_candidate_after_checks",
            descriptor,
            StringComparison.Ordinal);

        ownerBlock.Should().BeLessThan(firstExternalMutation);
        preUpdate.Should().BeLessThan(candidate);
        candidate.Should().BeLessThan(candidateSemantics);
        candidateSemantics.Should().BeLessThan(arm);
        candidate.Should().BeLessThan(arm);
        arm.Should().BeLessThan(forward);
        forward.Should().BeLessThan(imageVerifier);
        candidate.Should().BeLessThan(imageVerifier);
        imageVerifier.Should().BeLessThan(readiness);
        readiness.Should().BeLessThan(network);
        network.Should().BeLessThan(flags);
        flags.Should().BeLessThan(publicProbe);
        publicProbe.Should().BeLessThan(proxyProbe);
        proxyProbe.Should().BeLessThan(descriptor);
        descriptor.Should().BeLessThan(finalCandidate);
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
