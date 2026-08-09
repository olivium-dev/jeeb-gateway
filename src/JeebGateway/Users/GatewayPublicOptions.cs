namespace JeebGateway.Users;

// Gateway__PublicBaseUrl — the externally reachable origin this gateway serves on.
// Blank (the committed default) means avatar refs project to null; no host is ever hardcoded.
public sealed class GatewayPublicOptions
{
    public const string SectionName = "Gateway";

    public string PublicBaseUrl { get; init; } = string.Empty;
}
