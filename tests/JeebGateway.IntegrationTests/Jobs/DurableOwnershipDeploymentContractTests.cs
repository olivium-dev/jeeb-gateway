using System.Diagnostics;
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
        ("push_gateway_api_key", "add_rotated_secret push_gateway_api_key \"\\$PUSH_SECRET\""),
        ("bundler_cms_bearer_token", "add_rotated_secret bundler_cms_bearer_token \"\\$BUNDLER_SECRET\""),
        ("private_artifact_store_bearer_token", "add_rotated_secret private_artifact_store_bearer_token \"\\$ARTIFACT_SECRET\""),
        ("data_export_token_signing_key", "add_rotated_secret data_export_token_signing_key \"\\$EXPORT_SECRET\""),
        ("jeeb_gateway_job_token", "add_rotated_secret jeeb_gateway_job_token \"\\$JOB_SECRET\""),
        ("firebase_admin_json", "add_rotated_secret firebase_admin_json \"\\$FIREBASE_SECRET\"")
    ];

    private static readonly (string Target, string Invocation)[] StagingMountedCredentials =
    [
        ("jeeb_state_service_token", "add_rotated_secret \"$state_secret_name\" jeeb_state_service_token"),
        ("notification_service_token", "add_rotated_secret \"$notification_secret_name\" notification_service_token"),
        ("push_gateway_api_key", "add_rotated_secret \"$push_secret_name\" push_gateway_api_key"),
        ("settlement_service_token", "add_rotated_secret \"$settlement_secret_name\" settlement_service_token"),
        ("bundler_cms_bearer_token", "add_rotated_secret \"$bundler_secret_name\" bundler_cms_bearer_token"),
        ("jeeb_gateway_job_token", "add_rotated_secret \"$job_secret_name\" jeeb_gateway_job_token"),
        ("jeeb_gateway_jwt", "add_rotated_secret \"$jwt_secret_name\" jeeb_gateway_jwt"),
        ("jeeb_gateway_umjwt", "add_rotated_secret \"$umjwt_secret_name\" jeeb_gateway_umjwt"),
        ("realtime_guardian_secret", "add_rotated_secret \"$guardian_secret_name\" realtime_guardian_secret"),
        ("staging_wss_probe_mint_key", "add_rotated_secret \"$probe_secret_name\" staging_wss_probe_mint_key"),
        ("realtime_membership_ticket_key", "add_rotated_secret \"$membership_secret_name\" realtime_membership_ticket_key"),
        ("firebase_admin_json", "add_rotated_secret \"$firebase_secret_name\" firebase_admin_json"),
        ("jeeb_gateway_service_auth", "add_rotated_secret \"$service_auth_secret_name\" jeeb_gateway_service_auth")
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
        workflow.Should().Contain("PushNotificationServiceApi__GatewayApiKeyFile");
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

    [Theory]
    [InlineData("deploy-to-jeeb.yml")]
    [InlineData("jeeb-staging-deploy.yml")]
    public void Gateway_deploys_enable_the_single_push_owner_contract(string workflowName)
    {
        var workflow = Workflow(workflowName);

        workflow.Should().Contain("FeatureFlags__NotificationDurableWrite__Enabled");
        workflow.Should().Contain("FeatureFlags__NotificationOutboxMode");
        workflow.Should().Contain("FeatureFlags__PushDispatchMode");
        (workflow.Contains("FeatureFlags__PushDispatchMode='local'", StringComparison.Ordinal)
            || workflow.Contains(
                "add_env FeatureFlags__PushDispatchMode local",
                StringComparison.Ordinal)).Should().BeTrue();
        workflow.Should().Contain("upstream-authority");
        workflow.Should().Contain("PushNotificationServiceApi__GatewayApiKeyFile");
        workflow.Should().Contain("push_gateway_api_key");
    }

    [Theory]
    [InlineData("deploy-to-jeeb.yml")]
    [InlineData("jeeb-production-deploy.yml")]
    [InlineData("jeeb-staging-deploy.yml")]
    public void Gateway_deploys_reconcile_the_canonical_firebase_contract(string workflowName)
    {
        var workflow = Workflow(workflowName);

        workflow.Should().Contain("JeebFirebaseContract__SchemaVersion");
        workflow.Should().Contain("JeebFirebaseContract__ProjectId");
        workflow.Should().Contain("jeeb-5a293");
        workflow.Should().Contain("JeebFirebaseContract__ProjectNumber");
        workflow.Should().Contain("1051234312170");
        workflow.Should().Contain("JeebFirebaseContract__FirestoreDatabaseId");
        workflow.Should().Contain("(default)");
        workflow.Should().Contain("JeebFirebaseContract__ChatEnabled");
        workflow.Should().Contain("JeebFirebaseContract__PushProducer");
        workflow.Should().Contain("notification-service");
        workflow.Should().Contain("Firebase__Chat__ProjectId");
        workflow.Should().Contain("Firebase__Chat__ServiceAccountKeyPath");
        workflow.Should().Contain("/run/secrets/firebase_admin_json");
        workflow.Should().Contain("secrets.JEEB_FIREBASE_JSON");
        workflow.Should().Contain("scripts/validate-firebase-service-account.py");
        workflow.Should().Contain("firebase_admin_json");
        workflow.Should().Contain("[0-9a-f]{64}");
        workflow.Contains("Firestore__DatabaseId", StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();
        workflow.Contains("Firebase__FirestoreDatabaseId", StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();
        workflow.Contains("Firebase__Chat__FirestoreDatabaseId", StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();
        workflow.Should().Contain("scripts/validate-jeeb-firebase-contract.py");
    }

    [Theory]
    [InlineData("deploy-to-jeeb.yml", "jeeb_fb_ \"$firebase_digest\"")]
    [InlineData("jeeb-production-deploy.yml", "jeeb_production_fb_ \"$firebase_digest\"")]
    [InlineData("jeeb-staging-deploy.yml", "jeeb_staging_fb_ \"$firebase_digest\"")]
    public void Every_rollout_rotates_and_post_verifies_one_content_addressed_firebase_mount(
        string workflowName,
        string expectedSecretName)
    {
        var workflow = Workflow(workflowName);
        var credentialValidator = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "validate-firebase-service-account.py"));

        workflow.Should().Contain(expectedSecretName);
        workflow.Should().Contain("scripts/validate-firebase-service-account.py");
        workflow.Should().Contain("scripts/firebase-docker-secret-name.sh");
        workflow.Should().Contain("Firebase__Chat__ServiceAccountKeyPath");
        workflow.Should().Contain("/run/secrets/firebase_admin_json");
        credentialValidator.Should().Contain("credential type must be service_account");
        credentialValidator.Should().Contain("credential project_id must be");
        workflow.Should().NotContain("gateway_firebase_${GITHUB_RUN_ID}");
        var preflight = workflow.IndexOf(
            "scripts/validate-firebase-service-account.py", StringComparison.Ordinal);
        var firstExternalMutation = new[]
        {
            workflow.IndexOf("docker login", StringComparison.Ordinal),
            workflow.IndexOf("docker/build-push-action@", StringComparison.Ordinal),
            workflow.IndexOf("docker secret create", StringComparison.Ordinal),
        }.Where(index => index >= 0).Min();
        preflight.Should().BeLessThan(firstExternalMutation,
            "the protected Firebase document must be validated before external mutation");

        if (workflowName == "jeeb-staging-deploy.yml")
        {
            var candidateContract = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(), "scripts", "staging-gateway-candidate-contract.jq"));
            candidateContract.Should().Contain(
                "[.TaskTemplate.ContainerSpec.Secrets[]?\n    | select(.File.Name == \"firebase_admin_json\")] | length) == 1");
        }
        else
        {
            workflow.Should().Contain("firebase_admin_json=");
            workflow.Should().Contain("grep -Fxc firebase_admin_json");
            workflow.Should().Contain(":65532:65532:256");
        }
    }

    [Fact]
    public void Staging_secret_preflight_and_cleanup_are_bounded_and_preserve_primary_failure()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        var preflight = workflow.IndexOf("for planned_secret in", StringComparison.Ordinal);
        var lockAcquire = workflow.IndexOf("staging_gateway_lock_acquire", preflight,
            StringComparison.Ordinal);

        preflight.Should().BeGreaterThanOrEqualTo(0);
        preflight.Should().BeLessThan(lockAcquire,
            "every Docker secret name must be bounded before the deployment lock or remote mutation");
        workflow.Should().Contain("for created in \"${cleanup_secret_names[@]}\"");
        workflow.Should().Contain("if ! docker secret inspect '$created' >/dev/null 2>&1; then");
        workflow.Should().Contain(
            "if [ \"$lock_cleanup_ok\" != true ] && [ \"$status\" -eq 0 ]; then");
        workflow.Should().NotContain(
            "for created in \"$jwt_secret_name\" \"$umjwt_secret_name\"");
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
        workflow.Should().Contain("add_env Auth__Otp__Phone__EnforceRegion false");
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
    public void Jeeb_staging_workflow_is_gateway_only_provider_secret_minimal_and_protected()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        var openAiSecret = "secrets.OPENAI" + "_API_KEY";

        workflow.Should().Contain("Require supported protected staging mode");
        workflow.Should().Contain("if: ${{ inputs.provider_expand_verified != true }}");
        workflow.Should().Contain("Require designated staging owner");
        workflow.Should().Contain("[ \"$REPOSITORY\" = jeeb-gateway ]");
        workflow.Should().Contain("GITHUB_REF_PROTECTED: ${{ github.ref_protected }}");
        workflow.Should().Contain("[ \"$GITHUB_REF_PROTECTED\" = true ]");
        workflow.Should().Contain("environment: staging");
        workflow.Should().Contain("[ \"$(hostname -s)\" = \"olivium-ephemerals\" ]");
        workflow.Should().Contain("grep -Fxc \"192.168.2.20\"");
        workflow.Should().Contain("Selective gateway deploy requires an incumbent service");
        workflow.Should().Contain("FailureAction:\"pause\",Order:\"start-first\"");
        workflow.Should().NotContain("FailureAction:\"rollback\",Order:\"start-first\"");
        workflow.Should().NotContain("FailureAction:\"pause\",Order:\"stop-first\"");
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
        workflow.IndexOf("Require supported protected staging mode", StringComparison.Ordinal)
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
    public void Gateway_deploy_templates_require_protected_entry_gates_and_verify_exact_image(
        string workflowName)
    {
        var workflow = Workflow(workflowName);
        var verifier = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "verify-swarm-service-image.sh"));
        var automaticRollback = "--update-failure-action " + "rollback";
        var rollbackOption = "--" + "rollback-order";

        workflow.Should().Contain("steps.immutable.outputs.image");
        workflow.Should().Contain("scripts/verify-swarm-service-image.sh");
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
            workflow.Should().Contain("Require supported protected staging mode");
            workflow.Should().Contain("if: ${{ inputs.provider_expand_verified != true }}");
            workflow.Should().Contain("Require designated staging owner");
            workflow.IndexOf("Require designated staging owner", StringComparison.Ordinal)
                .Should().BeLessThan(workflow.IndexOf("actions/checkout@", StringComparison.Ordinal));
            workflow.Should().Contain("FailureAction:\"pause\",Order:\"start-first\"");
            workflow.Should().NotContain("FailureAction:\"rollback\",Order:\"start-first\"");
            workflow.Should().NotContain("FailureAction:\"pause\",Order:\"stop-first\"");
            workflow.Should().Contain("staging_gateway_external_gate_recover");
            workflow.Should().Contain("staging_gateway_forward_apply");
            workflow.Should().Contain("recovery_armed=true");
            workflow.Should().Contain("docker service inspect '$service' --format '{{json .Spec}}'");
            workflow.Should().Contain("docker service inspect '$service' --format '{{.ID}} {{.Version.Index}}'");
            workflow.Should().Contain(
                "staging_gateway_specs_equal \"$pre_update_spec\" \"$incumbent_spec\"");
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
            workflow.Should().Contain("Owner block - forward-only promotion pending");
            workflow.Should().Contain("::error::Forward-only promotion pending owner-approved failure handling");
            workflow.IndexOf("Owner block - forward-only promotion pending", StringComparison.Ordinal)
                .Should().BeLessThan(workflow.IndexOf("actions/checkout@", StringComparison.Ordinal));
            workflow.Should().Contain("--update-order start-first");
            workflow.Should().Contain("--update-failure-action pause");
            workflow.Should().Contain("--publish-rm \"\\$INT\"");
            workflow.Should().NotContain("--publish-rm \"\\$EXT\"");
            workflow.Should().Contain("mode=ingress");
            workflow.Should().NotContain(automaticRollback);
            workflow.Should().NotContain(rollbackOption);
            workflow.Should().NotContain("staging_gateway_external_gate_recover");
            workflow.Should().NotContain("--update-order stop-first");
            const string runtimeVerifier = "base64 -d | bash -s -- \"\\$SVC\" \"\\$REQUESTED_IMAGE\"";
            CountOccurrences(workflow, runtimeVerifier).Should().Be(1);
            workflow.Should().Contain("Deployed service spec does not match the requested immutable digest");
        }
    }

    [Fact]
    public void Gateway_host_publish_migration_removes_the_target_port_and_leaves_ingress_unchanged()
    {
        var workflow = Workflow("deploy-to-jeeb.yml");

        EvaluatePortMigration(workflow, "10000:8080:host").Should().Equal(
            "--publish-rm",
            "8080",
            "--publish-add",
            "published=10000,target=8080,mode=ingress");
        EvaluatePortMigration(workflow, "10000:8080:ingress").Should().BeEmpty();
    }

    [Fact]
    public void Jeeb_staging_is_a_protected_non_activating_full_spec_template()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");
        workflow.Should().Contain("Require supported protected staging mode");
        workflow.Should().Contain("if: ${{ inputs.provider_expand_verified != true }}");
        workflow.Should().Contain("Require designated staging owner");
        workflow.Should().Contain("FailureAction:\"pause\",Order:\"start-first\"");
        workflow.Should().NotContain("FailureAction:\"rollback\",Order:\"start-first\"");
        workflow.Should().NotContain("FailureAction:\"pause\",Order:\"stop-first\"");
        workflow.Should().NotContain("docker service " + "rollback");
        workflow.Should().NotContain("docker service update --detach=false");
        workflow.Should().Contain("staging_gateway_external_gate_recover");
        workflow.Should().Contain("staging_gateway_forward_apply");
        workflow.Should().Contain("recovery_armed=true");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Chat false");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Realtime false");
        workflow.Should().Contain("add_env Features__RealtimeWebSocketProxy__Enabled false");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Voice false");
        workflow.Should().Contain("add_env FeatureFlags__UseUpstream__Otp true");
        workflow.Should().NotContain("add_env FeatureFlags__UseUpstream__Chat true");
        workflow.Should().NotContain("add_env FeatureFlags__UseUpstream__Realtime true");
        workflow.Should().NotContain("add_env Features__RealtimeWebSocketProxy__Enabled true");
        workflow.Should().NotContain("add_env FeatureFlags__UseUpstream__Voice true");
        var dispatch = workflow[..workflow.IndexOf("permissions:", StringComparison.Ordinal)];
        dispatch.Should().Contain("deployment_mode:");
        dispatch.Should().Contain("default: normal");
        dispatch.Should().Contain("type: choice");
        dispatch.Should().Contain("- security-cutover");
        dispatch.Should().Contain("- otp-cutover");
        dispatch.Should().Contain("- devtool-reassert");
        CountOccurrences(dispatch, "inputs:").Should().Be(1);
        CountOccurrences(dispatch, "deployment_mode:").Should().Be(1);
        workflow.Should().NotContain("inputs.deployment_mode == 'normal'");
        workflow.Should().NotContain("${{ inputs.activate_");

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
        workflow.Should().Contain(
            "staging_gateway_specs_equal \"$pre_update_spec\" \"$incumbent_spec\"");
        workflow.Should().Contain("cmp -s \"$pre_update_version\" \"$incumbent_version\"");
        workflow.Should().Contain("cmp -s \"$pre_update_id\" \"$incumbent_id\"");
        workflow.Should().Contain("tolower($1) == tolower(expected)");
        workflow.Should().Contain("matches == 1 && exact_value == 1");
        workflow.Should().Contain("verify_exact_candidate_after_checks() {");
        workflow.Should().Contain(
            "bash scripts/staging-gateway-terminal-candidate-check.sh");
        workflow.Should().Contain("posture_mode=posture");
        workflow.Should().Contain("posture_mode=devtool-posture");
        workflow.Should().Contain("scripts/probe-staging-public-gateway-contract.sh");
        workflow.Should().Contain("group: jeeb-staging-gateway-mutation");
        workflow.Should().Contain("source scripts/staging-gateway-mutation-lock.sh");
        workflow.Should().Contain("source scripts/staging-gateway-security-cutover.sh");
        workflow.Should().Contain("scripts/staging-gateway-spec-canonicalization.sh");
        workflow.Should().Contain("staging_gateway_canonicalize_spec_file \"$snapshot\"");
        workflow.Should().Contain(
            "if: ${{ always() && steps.remote_ghcr_login.outcome != 'skipped' }}");
        workflow.Should().Contain("[ \"$status\" -ne 0 ] || status=99");
        workflow.Should().NotContain("append_sanitized_transaction_summary || status=99");
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
        workflow.Should().Contain(
            "add_env Auth__Otp__Phone__EnforceRegion false");
        workflow.Should().Contain(
            "JEEB_OTP_SERVICE_AUTH_KEY: ${{ secrets.JEEB_OTP_SERVICE_AUTH_KEY }}");
        workflow.Should().Contain(
            "add_env ServiceAuth__SigningKeyFile /run/secrets/jeeb_gateway_service_auth");
        workflow.Should().Contain(
            "add_rotated_secret \"$service_auth_secret_name\" jeeb_gateway_service_auth");
        workflow.Should().Contain(
            "Services__Realtime__GuardianSecret Operations__RealtimeProbe__MintKey \\\n" +
            "                  ServiceAuth__SigningKey Firestore__DatabaseId \\\n" +
            "                  Firebase__FirestoreDatabaseId Firebase__Chat__FirestoreDatabaseId \\\n" +
            "                  \"${retired_gateway_env[@]}\"");
        workflow.Should().Contain("scripts/staging-gateway-devtool-reassert-candidate.jq");
        workflow.Should().Contain("scripts/staging-gateway-public-edge-backoff.sh");
        workflow.Should().Contain(
            "staging phase=devtool-public-edge-stabilization result=started (redacted)");
        workflow.Should().NotContain(
            "add_env Services__Realtime__BaseUrl http://192.168.2.20:10069");
        workflow.Should().Contain("python3 scripts/probe-staging-authenticated-realtime.py");
        workflow.Should().Contain("probe_staging_untrusted_xff_contract");
        workflow.Should().Contain("scripts/probe-staging-untrusted-xff.sh");
        workflow.Should().Contain("ForwardedHeaders__KnownProxies__0");
        workflow.Should().NotContain("add_env ForwardedHeaders__KnownProxies");
        CountOccurrences(workflow, "bash scripts/verify-staging-otp-verify-freeze.sh")
            .Should().Be(3);
        workflow.Should().Contain("staging_gateway_security_cutover_forward_apply");
        workflow.Should().Contain("--execute capture");
        workflow.Should().Contain("--execute verify");
        workflow.Should().Contain("[ \"$recovery_armed\" = false ]");
        workflow.Should().Contain("[ \"$DEPLOYMENT_MODE\" != security-cutover ]");
        CountOccurrences(workflow, "scripts/verify-swarm-service-image.sh").Should().Be(2,
            "the protected template retains exact candidate and recovered-incumbent verifiers");

        var modeGate = workflow.IndexOf(
            "Require supported protected staging mode", StringComparison.Ordinal);
        var firstExternalMutation = workflow.IndexOf("docker/login-action@", StringComparison.Ordinal);
        var preUpdate = workflow.IndexOf(
            "capture_remote_spec \"$pre_update_spec\" \"$pre_update_version\" \"$pre_update_id\"",
            StringComparison.Ordinal);
        var candidate = workflow.IndexOf(
            "staging_gateway_canonicalize_spec_file \"$candidate_raw_spec\" \"$candidate_spec\"",
            preUpdate,
            StringComparison.Ordinal);
        var candidateSemantics = workflow.IndexOf(
            "-f scripts/staging-gateway-candidate-contract.jq",
            candidate,
            StringComparison.Ordinal);
        var taskCapture = workflow.IndexOf("--execute capture", candidateSemantics,
            StringComparison.Ordinal);
        var preCasFreeze = workflow.IndexOf(
            "bash scripts/verify-staging-otp-verify-freeze.sh",
            taskCapture,
            StringComparison.Ordinal);
        var cutoverForward = workflow.IndexOf(
            "staging_gateway_security_cutover_forward_apply \\",
            preCasFreeze,
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
        var flags = workflow.IndexOf("verify_bootstrap_flags", readiness, StringComparison.Ordinal);
        var network = workflow.IndexOf(
            "verify_staging_overlay_and_dns",
            flags,
            StringComparison.Ordinal);
        var publicProbe = workflow.IndexOf(
            "bash scripts/probe-staging-public-gateway-contract.sh invariant",
            network,
            StringComparison.Ordinal);
        var proxyProbe = workflow.IndexOf(
            "probe_staging_untrusted_xff_contract",
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
        var oldTaskProof = workflow.IndexOf("--execute verify", finalCandidate,
            StringComparison.Ordinal);
        var postFreeze = workflow.IndexOf(
            "bash scripts/verify-staging-otp-verify-freeze.sh",
            oldTaskProof,
            StringComparison.Ordinal);
        var finalConfirmation = workflow.IndexOf(
            "verify_exact_candidate_after_checks",
            postFreeze,
            StringComparison.Ordinal);

        modeGate.Should().BeLessThan(firstExternalMutation);
        preUpdate.Should().BeLessThan(candidate);
        candidate.Should().BeLessThan(candidateSemantics);
        candidateSemantics.Should().BeLessThan(taskCapture);
        taskCapture.Should().BeLessThan(preCasFreeze);
        preCasFreeze.Should().BeLessThan(cutoverForward);
        cutoverForward.Should().BeLessThan(arm);
        candidateSemantics.Should().BeLessThan(arm);
        candidate.Should().BeLessThan(arm);
        arm.Should().BeLessThan(forward);
        forward.Should().BeLessThan(imageVerifier);
        candidate.Should().BeLessThan(imageVerifier);
        imageVerifier.Should().BeLessThan(readiness);
        readiness.Should().BeLessThan(flags);
        flags.Should().BeLessThan(network);
        network.Should().BeLessThan(publicProbe);
        publicProbe.Should().BeLessThan(proxyProbe);
        proxyProbe.Should().BeLessThan(descriptor);
        descriptor.Should().BeLessThan(finalCandidate);
        finalCandidate.Should().BeLessThan(oldTaskProof);
        oldTaskProof.Should().BeLessThan(postFreeze);
        postFreeze.Should().BeLessThan(finalConfirmation);
    }

    [Fact]
    public void StagingCallerActivation_IsHeldUntilProtectedMainRelayExpandIsVerified()
    {
        var workflow = Workflow("jeeb-staging-deploy.yml");

        workflow.Should().Contain("provider_expand_verified");
        workflow.Should().Contain(
            "protected-main push-notification image in expand mode first");
        workflow.IndexOf("Hold caller activation", StringComparison.Ordinal)
            .Should().BeLessThan(workflow.IndexOf("Require designated staging owner", StringComparison.Ordinal));
    }

    [Fact]
    public void MsiCallerActivation_RetainsProviderExpandHoldBehindOwnerBlock()
    {
        var workflow = Workflow("deploy-to-jeeb.yml");

        workflow.Should().Contain("JEEB_PUSH_PROVIDER_EXPAND_VERIFIED");
        workflow.Should().Contain(
            "protected-main push-notification image in expand mode first");
        workflow.IndexOf("Owner block", StringComparison.Ordinal)
            .Should().BeLessThan(workflow.IndexOf("Hold caller activation", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> EvaluatePortMigration(string workflow, string currentPorts)
    {
        const string startMarker = "          port_args=()";
        const string endMarker = "\n          esac";
        var start = workflow.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start < 0
            ? -1
            : workflow.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("Missing host-to-ingress port migration branch.");

        // This is the exact branch transmitted through the outer heredoc; unescape
        // only its delayed remote-variable expansion before executing it locally.
        var branch = workflow[start..(end + endMarker.Length)]
            .Replace("\\$", "$", StringComparison.Ordinal);
        var script = string.Join('\n',
            "set -euo pipefail",
            "EXT=10000",
            "INT=8080",
            $"current_ports='{currentPorts}'",
            branch,
            "printf '%s\\n' \"${#port_args[@]}\"",
            "if ((${#port_args[@]})); then",
            "  for arg in \"${port_args[@]}\"; do",
            "    printf '%s\\n' \"$arg\"",
            "  done",
            "fi");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = "-s",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        if (!process.Start())
            throw new InvalidOperationException("Could not start bash for the port migration contract.");

        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);

        var arguments = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (arguments.Length == 0 || !int.TryParse(arguments[0], out var count)
                                  || count != arguments.Length - 1)
            throw new InvalidOperationException("Port migration branch emitted an invalid argument list.");

        return arguments[1..];
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
