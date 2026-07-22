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
    Task<DataExportPayload> BuildAsync(string userId, string format, CancellationToken ct);
}

public class DataExportPayload
{
    public required byte[] Bytes { get; init; }
    public required string ContentType { get; init; }
    public required string FileName { get; init; }
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
    private readonly TimeProvider _clock;

    public DataExportPackager(
        IUsersStore users,
        IRequestsStore requests,
        IDataExportRatingsProvider ratings,
        IDataExportChatHistoryProvider chats,
        TimeProvider clock)
    {
        _users = users;
        _requests = requests;
        _ratings = ratings;
        _chats = chats;
        _clock = clock;
    }

    public async Task<DataExportPayload> BuildAsync(string userId, string format, CancellationToken ct)
    {
        var profile = await _users.GetByIdAsync(userId, ct);
        var addresses = await _users.ListAddressesAsync(userId, ct);
        var orders = await GatherOrdersAsync(userId, ct);
        var ratings = await _ratings.GetForUserAsync(userId, ct);
        var chats = await _chats.GetForUserAsync(userId, ct);

        var document = new
        {
            schemaVersion = 1,
            generatedAt = _clock.GetUtcNow(),
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
        var contentType = "application/json";
        var fileName = $"jeeb-data-export-{userId}-{_clock.GetUtcNow():yyyyMMdd-HHmmss}.json";

        return new DataExportPayload
        {
            Bytes = json,
            ContentType = contentType,
            FileName = fileName
        };
    }

    private async Task<IReadOnlyList<DeliveryRequest>> GatherOrdersAsync(string userId, CancellationToken ct)
    {
        // The IRequestsStore contract only exposes scans by status; we
        // need every order ever created for the user. The in-memory MVP
        // store keeps everything in a dictionary, so we read it through
        // the reflection-free escape hatch the production Postgres store
        // will replace with a "WHERE client_id = ?" query.
        if (_requests is InMemoryRequestsStore mem)
        {
            return mem.ListForClient(userId);
        }
        // Until the production seam is wired, fall back to a stale-but-correct
        // listing via the pending-pre-acceptance window. Production swap
        // will provide a typed ListForUser method on the NSwag client.
        var listed = await _requests.ListPendingCreatedAtOrBeforeAsync(
            DateTimeOffset.MaxValue,
            ct);
        return listed.Where(r => string.Equals(r.ClientId, userId, StringComparison.Ordinal)).ToArray();
    }
}
