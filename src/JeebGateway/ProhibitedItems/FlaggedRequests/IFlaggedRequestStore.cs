using JeebGateway.ProhibitedItems.Scanner;

namespace JeebGateway.ProhibitedItems.FlaggedRequests;

public interface IFlaggedRequestStore
{
    Task<FlaggedRequest> CreateAsync(FlaggedRequestCreate input, CancellationToken ct);

    Task<FlaggedRequest?> GetAsync(string id, CancellationToken ct);

    Task<FlaggedRequestPage> ListAsync(
        FlaggedRequestStatus? status,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<FlaggedRequest?> DecideAsync(
        string id,
        FlaggedRequestStatus status,
        string adminUserId,
        string? note,
        CancellationToken ct);
}

public class FlaggedRequestCreate
{
    public required string? RequestId { get; init; }
    public required string UserId { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ProhibitedItemMatch> Matches { get; init; }
}

public class FlaggedRequestPage
{
    public required IReadOnlyList<FlaggedRequest> Items { get; init; }
    public required int Total { get; init; }
}
