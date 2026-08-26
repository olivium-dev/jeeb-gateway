namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 — configuration for the service-to-service auth header
/// (<c>X-Service-Auth</c>) attached by <see cref="ServiceAuthSigningHandler"/>
/// to every outbound BFF call.
///
/// Lifted from rahmah-gateway's <c>ServiceAuth</c> attribute pattern but
/// implemented as an outbound signer here because the BFF is the producer of
/// the header; downstream services are the consumers/verifiers (rahmah has
/// it the other way around — it verifies inbound headers and that is the
/// reason it does NOT forward to its own dependencies).
///
/// The shared secret comes from <c>ServiceAuth:SigningKeyFile</c> in Swarm
/// or the legacy inline <c>ServiceAuth:SigningKey</c> in native deployments.
/// The signed payload is
/// <c>"{caller}|{unix-seconds}|{nonce}|{verb} {path}"</c> (HMAC-SHA256,
/// Base64), so an attacker who intercepts a header cannot replay it against
/// a different method/path and cannot reuse it past
/// <see cref="ClockSkewSeconds"/>.
/// </summary>
public sealed class ServiceAuthOptions
{
    public const string SectionName = "ServiceAuth";

    /// <summary>
    /// Identifier broadcast in the signed header so downstream verifiers can
    /// look up the allow-list entry. Defaults to <c>"jeeb-gateway"</c>.
    /// </summary>
    public string Caller { get; set; } = "jeeb-gateway";

    /// <summary>
    /// HMAC-SHA256 shared secret. Must be ≥ 32 chars when enabled. Native
    /// deployments may continue to inject it inline; Swarm uses
    /// <see cref="SigningKeyFile"/> so key material is absent from the service
    /// environment and service specification.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional absolute path to a mounted secret file. When configured it
    /// takes precedence over <see cref="SigningKey"/>.
    /// </summary>
    public string SigningKeyFile { get; set; } = string.Empty;

    /// <summary>
    /// Window of acceptable timestamp drift on the verifier side. The signer
    /// does not enforce this — it is documented here so the producer and
    /// verifier agree on the same constant when the matching verifier is
    /// shipped in downstream services.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>
    /// When false the handler degrades to attaching only the caller +
    /// timestamp header without an HMAC body. Lets local dev environments
    /// skip key wiring; production MUST leave it true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
