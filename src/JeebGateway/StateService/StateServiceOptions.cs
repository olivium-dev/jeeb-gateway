using System.ComponentModel.DataAnnotations;

namespace JeebGateway.StateService;

/// <summary>
/// Configuration for the NSwag-typed client that backs the gateway's durable
/// store interfaces against <c>jeeb-state-service</c> (ADR-001-rev2). The
/// gateway stays stateless: every persisted row lives behind this client.
/// </summary>
public sealed class StateServiceOptions
{
    public const string SectionName = "JeebStateService";

    /// <summary>
    /// Base URL of jeeb-state-service, e.g. <c>http://192.168.2.50:10073</c>.
    /// Supplied via swarm env/config — never committed.
    /// </summary>
    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Per-call timeout. Kept short so a state-service blip degrades the
    /// gateway gracefully via the circuit breaker rather than blocking
    /// request threads (ADR-001-rev2 negative-consequence mitigation).
    /// </summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Absolute path of the Docker/Swarm secret shared with jeeb-state-service.
    /// The credential itself is never accepted from appsettings or an env value.
    /// </summary>
    public string ServiceTokenFile { get; init; } = string.Empty;

    /// <summary>
    /// Master switch. When false (or BaseUrl unset), state-owned production
    /// surfaces fail closed; explicit development/test harnesses may register
    /// local fakes for unrelated legacy contracts.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Absolute path to the mounted shared-secret file for jeeb-state-service ownership auth
    /// (env key <c>JeebStateService__ServiceTokenFile</c>). Unset leaves every state HttpClient
    /// unauthenticated, exactly as today — the credential is additive, not a new hard dependency.
    /// </summary>
    public string? ServiceTokenFile { get; init; }

    public bool HasServiceCredential => !string.IsNullOrWhiteSpace(ServiceTokenFile);
}
