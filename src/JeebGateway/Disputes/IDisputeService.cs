namespace JeebGateway.Disputes;

/// <summary>
/// Dispute orchestration (T-backend-025 / JEEB-43).
///
/// <list type="bullet">
///   <item><see cref="FileAsync"/> validates the request, persists the row
///     in state <see cref="DisputeState.Filed"/>, and fans out a push to
///     the admin queue so a reviewer can pick it up.</item>
///   <item><see cref="GetAsync"/> returns a single dispute for the caller
///     when they filed it (or any dispute when the caller is admin).</item>
///   <item><see cref="ListForUserAsync"/> returns every dispute the caller
///     filed.</item>
///   <item><see cref="ResolveAsync"/> applies an admin's verdict
///     (open / resolve / dismiss) and pushes the outcome to the filer.</item>
/// </list>
/// </summary>
public interface IDisputeService
{
    Task<Dispute> FileAsync(FileDisputeInput input, CancellationToken ct);

    Task<Dispute?> GetAsync(string disputeId, CancellationToken ct);

    Task<IReadOnlyList<Dispute>> ListForUserAsync(string userId, CancellationToken ct);

    Task<Dispute?> ResolveAsync(string disputeId, ResolveDisputeInput input, CancellationToken ct);
}

public class FileDisputeInput
{
    public required string DeliveryId { get; init; }
    public required string FiledByUserId { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();
}

public enum DisputeResolveAction
{
    Open,
    Resolve,
    Dismiss
}

public class ResolveDisputeInput
{
    public required DisputeResolveAction Action { get; init; }
    public required string AdminUserId { get; init; }
    public string? Resolution { get; init; }
}

public class DisputeValidationException : Exception
{
    public DisputeValidationException(string message) : base(message) { }
}

public class DisputeConflictException : Exception
{
    public DisputeConflictException(string message) : base(message) { }
}
