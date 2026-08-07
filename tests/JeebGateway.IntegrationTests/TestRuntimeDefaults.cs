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
        Environment.SetEnvironmentVariable("AdminOidc__Enabled", "true");
        Environment.SetEnvironmentVariable("AdminOidc__Issuer", "https://identity.example.test");
        Environment.SetEnvironmentVariable(
            "AdminOidc__AuthorizationEndpoint", "https://identity.example.test/authorize");
        Environment.SetEnvironmentVariable(
            "AdminOidc__TokenEndpoint", "https://identity.example.test/token");
        Environment.SetEnvironmentVariable(
            "AdminOidc__JwksUri", "https://identity.example.test/jwks");
        Environment.SetEnvironmentVariable("AdminOidc__ClientId", "integration-test-admin");
        Environment.SetEnvironmentVariable("AdminOidc__ClientSecret", "integration-test-secret");
        Environment.SetEnvironmentVariable(
            "AdminOidc__RedirectUri",
            "https://admin.jeeb.example/gateway/admin/v1/auth/oidc/callback");
        Environment.SetEnvironmentVariable(
            "AdminOidc__StateProtectionKey",
            Convert.ToBase64String(Enumerable.Repeat((byte)11, 32).ToArray()));
        Environment.SetEnvironmentVariable(
            "AdminOidc__RoleMappings__support__0", "integration-support");
        Environment.SetEnvironmentVariable(
            "Settlements__CodOwnerVerified", "true");
    }
}
