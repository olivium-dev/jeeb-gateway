using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Requests;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Builds the bytes that will be served to the user. Encapsulates the
/// "what goes in the export" question so the processor stays focused on
/// the state machine. AC: the payload MUST include profile, orders,
/// ratings, and chat history (T-backend-042).
/// </summary>
public interface IDataExportPackager
{
    Task<DataExportPayload> BuildAsync(
        string userId,
        string format,
        DateTimeOffset generatedAt,
        CancellationToken ct);
}

public class DataExportPayload
{
    public required byte[] Bytes { get; init; }
    public required string ContentType { get; init; }
    public required string FileName { get; init; }
}

public sealed record DataExportPackageMetadata(string ContentType, string FileName)
{
    public static DataExportPackageMetadata For(
        string userId,
        string format,
        DateTimeOffset generatedAt)
    {
        // PDF is still an accepted compatibility request but is packaged as
        // honest JSON until a PDF renderer owner exists.
        _ = format;
        return new DataExportPackageMetadata(
            "application/json",
            $"jeeb-data-export-{userId}-{generatedAt.ToUniversalTime():yyyyMMdd-HHmmss}.json");
    }
}

public class DataExportPackager : IDataExportPackager
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IUsersStore _users;
    private readonly IRequestsStore _requests;
    private readonly IDataExportRatingsProvider _ratings;
    private readonly IDataExportChatHistoryProvider _chats;
    public DataExportPackager(
        IUsersStore users,
        IRequestsStore requests,
        IDataExportRatingsProvider ratings,
        IDataExportChatHistoryProvider chats)
    {
        _users = users;
        _requests = requests;
        _ratings = ratings;
        _chats = chats;
    }

    public async Task<DataExportPayload> BuildAsync(
        string userId,
        string format,
        DateTimeOffset generatedAt,
        CancellationToken ct)
    {
        var profile = await _users.GetByIdAsync(userId, ct);
        var addresses = await _users.ListAddressesAsync(userId, ct);
        var orders = await GatherOrdersAsync(userId, ct);
        var ratings = await _ratings.GetForUserAsync(userId, ct);
        var chats = await _chats.GetForUserAsync(userId, ct);

        var document = new
        {
            schemaVersion = 1,
            generatedAt = generatedAt.ToUniversalTime(),
            userId,
            profile = profile is null ? null : new
            {
                profile.Id,
                profile.Phone,
                profile.Email,
                profile.Name,
                profile.AvatarUrl,
                profile.Language,
                profile.Roles,
                profile.Rating,
                profile.RatingCount,
                profile.CreatedAt,
                profile.UpdatedAt
            },
            savedAddresses = addresses.Select(a => new
            {
                a.Id,
                a.Label,
                a.Line1,
                a.Line2,
                a.City,
                a.Country,
                a.Latitude,
                a.Longitude,
                a.IsDefault,
                a.CreatedAt,
                a.UpdatedAt
            }),
            orders = orders.Select(o => new
            {
                o.Id,
                o.Status,
                o.Description,
                o.PickupAddress,
                o.DropoffAddress,
                o.CreatedAt,
                o.ExpiredAt
            }),
            ratings = ratings.Select(r => new
            {
                r.RatingId,
                r.RequestId,
                r.Direction,
                r.CounterpartyId,
                r.Stars,
                r.Comment,
                r.CreatedAt
            }),
            chatHistory = chats.Select(m => new
            {
                m.ConversationId,
                m.MessageId,
                m.SenderId,
                m.Body,
                m.SentAt
            })
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(document, Json);

        // PDF rendering is not on the MVP critical path. The API accepts
        // `pdf` so the contract matches the AC; the actual payload is
        // still JSON until the renderer service is wired up. The bytes
        // are still valid (a PDF reader will reject them), but the
        // ContentType is honest so clients don't mis-render.
        var metadata = DataExportPackageMetadata.For(userId, format, generatedAt);

        return new DataExportPayload
        {
            Bytes = json,
            ContentType = metadata.ContentType,
            FileName = metadata.FileName
        };
    }

    private async Task<IReadOnlyList<DeliveryRequest>> GatherOrdersAsync(string userId, CancellationToken ct)
    {
        // A right-of-access package requires every historical delivery in both
        // roles. Expiry/pending scans are not substitutes: filtering them would
        // silently omit completed and cancelled orders. DeliveryOwnerRequestsStore
        // therefore raises OwnerCapabilityUnavailableException until delivery-
        // service exposes complete client- and jeeber-scoped list operations;
        // DataExportWorkHandler converts that gap into a durable defer.
        var asClient = await _requests.ListForClientAsync(userId, ct);
        var asJeeber = await _requests.ListForJeeberAsync(userId, ct);
        return asClient.Concat(asJeeber)
            .GroupBy(request => request.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(request => request.CreatedAt)
            .ThenBy(request => request.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
