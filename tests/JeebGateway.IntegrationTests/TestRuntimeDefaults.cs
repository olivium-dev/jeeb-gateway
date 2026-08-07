using System.Runtime.CompilerServices;

namespace JeebGateway.IntegrationTests;

internal static class TestRuntimeDefaults
{
    [ModuleInitializer]
    internal static void ConfigureEvidenceKey()
    {
        // WebApplicationFactory defaults to Production in several legacy test
        // fixtures. Supply a test-only dedicated key so those hosts can exercise
        // unrelated behavior while the real deploy workflow remains fail-closed.
        Environment.SetEnvironmentVariable(
            "AdminEvidence__TokenKey",
            "integration-test-admin-evidence-key-32-bytes-minimum");
        // Extraction adaptation: AdminOidc stays ambient-OFF so legacy fixtures
        // keep main's admin-surface behavior; OIDC fixtures opt in per factory.
    }
}
