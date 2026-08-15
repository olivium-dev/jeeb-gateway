namespace JeebGateway.Infrastructure;

/// <summary>
/// A gateway operation has no durable route on its owning service yet.
/// Throwing is intentional: the gateway must never fabricate success or retain
/// authoritative data locally while an owner contract is incomplete.
/// </summary>
public sealed class OwnerCapabilityUnavailableException : Exception
{
    public OwnerCapabilityUnavailableException(string capability)
        : base($"The owning service does not expose the required capability: {capability}.")
    {
        Capability = capability;
    }

    public string Capability { get; }
}
