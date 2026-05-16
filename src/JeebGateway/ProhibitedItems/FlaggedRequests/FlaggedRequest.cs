using JeebGateway.ProhibitedItems.Scanner;

namespace JeebGateway.ProhibitedItems.FlaggedRequests;

public enum FlaggedRequestStatus
{
    Pending,
    Cleared,
    Upheld
}

public class FlaggedRequest
{
    public required string Id { get; init; }
    public required string? RequestId { get; init; }
    public required string UserId { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ProhibitedItemMatch> Matches { get; init; }
    public required FlaggedRequestStatus Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}
