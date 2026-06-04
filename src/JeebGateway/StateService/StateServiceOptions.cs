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
    /// Master switch. When false (or BaseUrl unset) the gateway falls back to
    /// the legacy in-memory stores. This keeps the rewire additive and lets
    /// local/CI runs stand up without a live state-service.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
