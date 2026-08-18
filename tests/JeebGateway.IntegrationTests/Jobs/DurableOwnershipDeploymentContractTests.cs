using FluentAssertions;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class DurableOwnershipDeploymentContractTests
{
    private static readonly string[] ProductionMountedCredentialTargets =
    [
        "jeeb_state_service_token",
        "bundler_cms_bearer_token",
        "private_artifact_store_bearer_token",
        "data_export_token_signing_key",
        "jeeb_gateway_job_token"
    ];

    [Theory]
    [InlineData("deploy-to-jeeb.yml")]
    [InlineData("jeeb-staging-deploy.yml")]
    public void Gateway_deploys_mount_every_owner_credential_as_a_versioned_secret(
        string workflowName)
    {
        var workflow = Workflow(workflowName);

        var targets = workflowName == "jeeb-staging-deploy.yml"
            ? new[]
            {
                "jeeb_state_service_token",
                "bundler_cms_bearer_token",
                "jeeb_gateway_job_token"
            }
            : ProductionMountedCredentialTargets;

        foreach (var target in targets)
            workflow.Should().Contain(target);

        if (workflowName != "jeeb-staging-deploy.yml")
            foreach (var target in targets)
                workflow.Should().Contain($"target={target},mode=0444");

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

        workflow.Should().Contain("retired_gateway_env=(UnifiedPaymentGateway__BaseUrl)");
        workflow.Should().Contain("\"${retired_gateway_env[@]}\"");
        workflow.Should().Contain("env_remove_args+=(--env-rm \"$stale_env\")");
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
