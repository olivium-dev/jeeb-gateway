namespace JeebGateway.Disputes;

/// <summary>
/// Persistence seam for disputes (T-backend-025). MVP backs this with an
/// in-memory ConcurrentDictionary; production swaps in a Postgres-backed
/// implementation colocated with admin moderation tables.
/// </summary>
public interface IDisputeStore
{
    Task<Dispute> AddAsync(Dispute dispute, CancellationToken ct);

    Task<Dispute?> GetByIdAsync(string disputeId, CancellationToken ct);

    Task<IReadOnlyList<Dispute>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Returns the currently-open dispute for a delivery (state is not in
    /// <see cref="DisputeState.TerminalStates"/>), or null if no such row
    /// exists. Used to enforce "one open dispute per delivery".
    /// </summary>
    Task<Dispute?> GetOpenForDeliveryAsync(string deliveryId, CancellationToken ct);

    /// <summary>
    /// Persists a state change. Returns null when the id is unknown.
    /// </summary>
    Task<Dispute?> UpdateStateAsync(string disputeId, DisputeStatePatch patch, CancellationToken ct);
}

public class DisputeStatePatch
{
    public required string State { get; init; }
    public required DateTimeOffset ReviewedAt { get; init; }
    public required string ResolverAdminId { get; init; }
    public string? Resolution { get; init; }
}
