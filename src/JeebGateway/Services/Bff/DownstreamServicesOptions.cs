namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC1 — declares which downstream services the BFF is
/// required to have a BaseUrl for in a given environment. Startup runs the
/// validator in <see cref="BffStartupValidator"/> against this list; a
/// missing key fails the boot with a structured error naming the missing
/// config path.
///
/// Defaults match the services already registered in
/// <see cref="Extensions.ServiceClientExtensions.AddDownstreamClients"/>.
/// Tests override the list when they want to assert the AC1 failure path
/// without standing up every downstream URL.
///
/// Why this is opt-in (RequiredInProduction) rather than always-on: dev and
/// integration test environments deliberately omit URLs for services they
/// are not exercising. Making the validator unconditional would block the
/// xUnit/WebApplicationFactory bootstrap.
/// </summary>
public sealed class DownstreamServicesOptions
{
    public const string SectionName = "BffServices";

    /// <summary>
    /// When true the validator enforces presence of every entry in
    /// <see cref="Required"/>. Defaults to true so a production deploy
    /// without explicit override fails closed.
    /// </summary>
    public bool RequiredInProduction { get; set; } = true;

    /// <summary>
    /// Set of <c>Services:{Section}:BaseUrl</c> sub-section names that the
    /// gateway must have configured. Validator throws a structured error
    /// listing every missing one (not just the first) so operators get a
    /// single round trip on the fix.
    /// </summary>
    // wallet-service is intentionally absent: it is wired in Program.cs as a
    // salehly-mirrored named client bound to the top-level WalletServiceApi:BaseUrl
    // key (not a Services:{Section} downstream), so it is not validated here.
    // NOTE: chat-service is intentionally NOT listed here. Chat moved to the
    // salehly-style top-level ChatServiceApi:BaseUrl key (consumed directly by the
    // NSwag ServiceChatClient in Program.cs) and is no longer a Services:* nested
    // downstream client, so this Services:{section}:BaseUrl validator does not
    // cover it.
    public List<string> Required { get; set; } = new()
    {
        "Auth",
        "UserManagement",
        "Matching",
        "Notification",
        "Geolocation",
        "PushNotification",
        "Delivery",
    };
}
