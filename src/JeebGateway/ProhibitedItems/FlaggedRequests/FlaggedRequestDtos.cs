using JeebGateway.ProhibitedItems.Scanner;

namespace JeebGateway.ProhibitedItems.FlaggedRequests;

public class ScanRequest
{
    public string? RequestId { get; set; }
    public string? Description { get; set; }
}

public class ScanResponse
{
    public required IReadOnlyList<MatchDto> Matches { get; init; }
    public required bool RequiresReview { get; init; }
    /// <summary>Set when RequiresReview is true; null otherwise.</summary>
    public string? FlaggedRequestId { get; init; }
    /// <summary>Always false in MVP; preserved so downstream callers can rely on the shape.</summary>
    public required bool AutoBlocked { get; init; }
}

public class MatchDto
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string Category { get; init; }
    public required string MatchedTerm { get; init; }
    public required string Evidence { get; init; }
    public required string MatchType { get; init; }
    public required double Confidence { get; init; }
}

public class FlaggedRequestDto
{
    public required string Id { get; init; }
    public required string? RequestId { get; init; }
    public required string UserId { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<MatchDto> Matches { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecisionNote { get; init; }
}

public class FlaggedRequestListResponse
{
    public required IReadOnlyList<FlaggedRequestDto> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

public class FlaggedRequestDecisionRequest
{
    public string? Decision { get; set; }
    public string? Note { get; set; }
}

public static class FlaggedRequestMapping
{
    public static MatchDto ToDto(this ProhibitedItemMatch m) => new()
    {
        ItemId = m.ItemId,
        ItemName = m.ItemName,
        Category = m.Category,
        MatchedTerm = m.MatchedTerm,
        Evidence = m.Evidence,
        MatchType = m.MatchType.ToString().ToLowerInvariant(),
        Confidence = Math.Round(m.Confidence, 4)
    };

    public static FlaggedRequestDto ToDto(this FlaggedRequest f) => new()
    {
        Id = f.Id,
        RequestId = f.RequestId,
        UserId = f.UserId,
        Description = f.Description,
        Matches = f.Matches.Select(ToDto).ToList(),
        Status = f.Status.ToString().ToLowerInvariant(),
        CreatedAt = f.CreatedAt,
        DecidedBy = f.DecidedBy,
        DecidedAt = f.DecidedAt,
        DecisionNote = f.DecisionNote
    };
}
