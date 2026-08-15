using FluentAssertions;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class DurableOwnershipDeploymentContractTests
{
    private static readonly string[] MountedCredentialTargets =
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

        foreach (var target in MountedCredentialTargets)
            workflow.Should().Contain(target);

        if (workflowName == "jeeb-staging-deploy.yml")
            workflow.Should().Contain("target=$target_name,mode=0444");
        else
            foreach (var target in MountedCredentialTargets)
                workflow.Should().Contain($"target={target},mode=0444");

        workflow.Should().Contain("JeebStateService__ServiceTokenFile");
        workflow.Should().Contain("/run/secrets/jeeb_state_service_token");
        workflow.Should().Contain("BUNDLER_CMS_BEARER_TOKEN_FILE");
        workflow.Should().Contain("PRIVATE_ARTIFACT_STORE_BEARER_TOKEN_FILE");
        workflow.Should().Contain("DATA_EXPORT_TOKEN_SIGNING_KEY_FILE");
        workflow.Should().Contain("InternalJobAuth__TokenFile");
        workflow.Should().NotContain("JeebStateService__ServiceToken=");
        workflow.Should().NotContain("InternalJobAuth__Token=");
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
