using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Caps = JeebGateway.Auth.Capabilities.Capabilities;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// iter5 DEFECT-2 — the Orders/Jobs LIST surfaces the mobile app calls but the gateway
/// never registered, so the live capture saw:
/// <list type="bullet">
///   <item><c>GET /v1/requests?role=client</c> → 405 (only POST /v1/requests +
///     GET /v1/requests/{id}(/offers) existed on that path).</item>
///   <item><c>GET /v1/deliveries</c> → 404 (no list route; only
///     GET /v1/deliveries/{id} existed).</item>
/// </list>
///
/// <para>THIN / STATELESS / EMPTY-TOLERANT. These are role-scoped reads of the caller's OWN
/// orders / assigned deliveries from the gateway's own request store (the same store
/// <see cref="JeebRequestsController"/> writes via POST /v1/requests and the offer-accept
/// path stamps a JeeberId onto). The gateway invents no new state. A store hiccup degrades to
/// an EMPTY paged envelope — these reads NEVER 5xx (the mobile Orders/Jobs tabs must not error).</para>
///
/// <para>RESPONSE SHAPE: every mobile list repository expects a PAGED ENVELOPE with the array
/// under the key <c>items</c> ({ items:[...], page, pageSize, totalCount, totalPages }) — never a
/// bare array, never data/requests/deliveries. This mirrors the existing
/// <c>JeebNotificationsPageResponse</c> envelope so the surface is consistent.</para>
///
/// <para>Distinct from PR #225's <c>GET /jeebers/me/feed</c> (the driver AVAILABLE-jobs broadcast
/// feed). This defect is the user's OWN orders (as client) and OWN assigned deliveries (as jeeber).</para>
/// </summary>
[ApiController]
public sealed class JeebOrdersListController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly IRequestsStore _requests;
    private readonly IPendingOffersStore _offers;
    private readonly ILogger<JeebOrdersListController> _log;

    public JeebOrdersListController(
        IRequestsStore requests,
        IPendingOffersStore offers,
        ILogger<JeebOrdersListController> log)
    {
        _requests = requests;
        _offers = offers;
        _log = log;
    }

    /// <summary>
    /// GET /v1/requests?role=client|jeeber&amp;status=pending&amp;page&amp;pageSize.
    /// <para>
    /// THREE scopes, selected by the query string (identity ALWAYS the bearer; never a body/query param):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Available-jobs feed</b> (<c>status=pending</c> / <c>status=matched</c>, no
    ///     <c>role=client</c>): the CROSS-CLIENT open-broadcast bucket a jeeber bids on — every
    ///     pre-acceptance request NOT created by the caller and NOT yet assigned. This is the jeeber
    ///     dashboard's available-jobs poll (iter6 B1). Sourced from the same gateway request store via
    ///     <see cref="IRequestsStore.ListPendingCreatedAtOrBeforeAsync"/> (the cross-client pre-acceptance
    ///     scan the expiry sweeper already uses) — the gateway invents no new state.</item>
    ///   <item><b>Assigned jobs</b> (<c>role=jeeber</c>): requests where THIS user is the assigned jeeber.</item>
    ///   <item><b>Own orders</b> (default / <c>role=client</c>): the requests this user created (Orders tab).</item>
    /// </list>
    /// Always 200 with a paged envelope (empty items when none / on a store blip) — NEVER 405/5xx.
    /// </summary>
    [HttpGet("v1/requests", Order = 2)]
    [RequireCapability(Caps.DeliveryParticipate)] // coarse {client, jeeber}; ownership scoped in-handler
    [ProducesResponseType(typeof(PagedListResponse<OrderListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListRequests(
        [FromQuery] string? role,
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        var jeeberScope = string.Equals(role, "jeeber", StringComparison.OrdinalIgnoreCase);
        var clientScope = string.Equals(role, "client", StringComparison.OrdinalIgnoreCase);

        // iter6 B1 — the jeeber dashboard available-jobs feed. The mobile feed
        // (DioRequestFeedRepository) polls GET /v1/requests?status=pending expecting the
        // CROSS-CLIENT open-broadcast bucket. When `status` selects a pre-acceptance state
        // (pending/matched) AND the caller did not explicitly ask for their own client list
        // (role=client), serve the available-jobs feed instead of the owner-scoped Orders list.
        var feedScope = !clientScope
            && !jeeberScope
            && !string.IsNullOrWhiteSpace(status)
            && RequestStatus.IsPreAcceptance(status!.Trim().ToLowerInvariant());

        if (feedScope)
            return await ListAvailableJobsFeedAsync(userId, page, pageSize, ct);

        IReadOnlyList<DeliveryRequest> rows;
        try
        {
            if (jeeberScope)
            {
                // Jeeber scope: requests where THIS user is the ASSIGNED jeeber (the accepted/active
                // deliveries the driver is working). iter6 jeeber-active-delivery: read the
                // jeeber-scoped store method (WHERE JeeberId == caller) instead of filtering the
                // owner-scoped client list — that list only holds rows the caller created AS A CLIENT,
                // so a jeeber (never the client of their assigned jobs) always got an empty list,
                // stranding them out of their accepted order's chat + delivery.
                rows = await _requests.ListForJeeberAsync(userId, ct);
            }
            else
            {
                rows = await _requests.ListForClientAsync(userId, ct);
            }
        }
        catch (Exception ex)
        {
            // NEVER 5xx the Orders tab — degrade to an empty page on any store fault.
            _log.LogWarning(ex, "v1/requests list read failed for {UserId}; serving empty page", userId);
            rows = Array.Empty<DeliveryRequest>();
        }

        var (pg, sz) = NormalizePaging(page, pageSize);
        var total = rows.Count;
        var window = rows
            .OrderByDescending(r => r.CreatedAt)
            .Skip((pg - 1) * sz)
            .Take(sz)
            .ToList();

        var items = new List<OrderListItem>(window.Count);
        foreach (var r in window)
        {
            int offersCount = 0;
            try
            {
                // offer-service's GET-offers is owner-gated; only the owning CLIENT
                // may read a request's bids. Pass the owner id only in client scope
                // (r.ClientId == userId there); in jeeber scope the caller is the
                // assigned driver, not the owner, so the offers count stays 0.
                var ownerForList = jeeberScope ? null : userId;
                var offers = await _offers.ListForRequestAsync(r.Id, ct, ownerForList);
                offersCount = offers.Count;
            }
            catch
            {
                // Best-effort decoration only; a missing offer count never fails the list.
            }
            items.Add(ToOrderItem(r, offersCount));
        }

        return Ok(PagedListResponse<OrderListItem>.Of(items, pg, sz, total));
    }

    /// <summary>
    /// iter6 B1 — the CROSS-CLIENT available-jobs feed for the jeeber dashboard.
    /// Returns the open-broadcast bucket: every pre-acceptance (pending/matched) request
    /// that the caller did NOT create and that is NOT yet assigned to a jeeber. Sourced from
    /// the gateway request store's pre-acceptance scan (<see cref="IRequestsStore.ListPendingCreatedAtOrBeforeAsync"/>),
    /// the same cross-client read the expiry sweeper uses — the gateway invents no new state and
    /// holds no feed projection. Each row carries pickup/dropoff with address + lat/lng so the
    /// mobile feed parser (which drops a row when either location lacks lat/lng) renders it.
    /// Always 200 with a paged envelope (empty on a store blip) — NEVER 5xx.
    /// </summary>
    private async Task<IActionResult> ListAvailableJobsFeedAsync(
        string userId,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        IReadOnlyList<DeliveryRequest> rows;
        try
        {
            // "created at or before now" = every currently-open pre-acceptance request,
            // cross-client (the store filters by status, not by owner). Exclude the caller's
            // own requests (you cannot bid on your own order) and any already-assigned row.
            var open = await _requests.ListPendingCreatedAtOrBeforeAsync(DateTimeOffset.UtcNow, ct);
            rows = open
                .Where(r => !string.Equals(r.ClientId, userId, StringComparison.Ordinal)
                            && string.IsNullOrEmpty(r.JeeberId))
                .ToList();
        }
        catch (Exception ex)
        {
            // NEVER 5xx the jeeber dashboard — degrade to an empty feed on any store fault.
            _log.LogWarning(ex, "v1/requests available-jobs feed read failed for {UserId}; serving empty feed", userId);
            rows = Array.Empty<DeliveryRequest>();
        }

        var (pg, sz) = NormalizePaging(page, pageSize);
        var total = rows.Count;
        var window = rows
            .OrderByDescending(r => r.CreatedAt)
            .Skip((pg - 1) * sz)
            .Take(sz)
            .Select(ToFeedItem)
            .ToList();

        return Ok(PagedListResponse<OrderListItem>.Of(window, pg, sz, total));
    }

    /// <summary>
    /// GET /v1/deliveries?page&amp;pageSize — the caller's OWN assigned deliveries (Jobs tab).
    /// A delivery is an accepted request stamped with this user's JeeberId. Identity ALWAYS from the
    /// bearer. Always 200 with a paged envelope (empty when none / on a store blip) — NEVER 404/5xx.
    /// </summary>
    [HttpGet("v1/deliveries")]
    [RequireCapability(Caps.DeliveryParticipate)] // {client, jeeber}
    [ProducesResponseType(typeof(PagedListResponse<OrderListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListDeliveries(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        IReadOnlyList<DeliveryRequest> rows;
        try
        {
            // The gateway's request store is the source for the user's own rows. Deliveries the user
            // is assigned to as jeeber are rows where JeeberId == caller; deliveries the user owns as
            // client are their own created rows that have reached a delivery stage. iter6
            // jeeber-active-delivery: union the jeeber-scoped read (WHERE JeeberId == caller — the
            // previously-missing half that made the jeeber's Jobs tab always empty) with the
            // owner-scoped client read, deduped by request id, so the Jobs tab shows the caller's
            // deliveries from whichever side they participate.
            var asJeeber = await _requests.ListForJeeberAsync(userId, ct);
            var asClient = await _requests.ListForClientAsync(userId, ct);
            rows = asJeeber
                .Concat(asClient)
                .GroupBy(r => r.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            // NEVER 404/5xx the Jobs tab — degrade to an empty page on any store fault.
            _log.LogWarning(ex, "v1/deliveries list read failed for {UserId}; serving empty page", userId);
            rows = Array.Empty<DeliveryRequest>();
        }

        var (pg, sz) = NormalizePaging(page, pageSize);
        var total = rows.Count;
        var window = rows
            .OrderByDescending(r => r.CreatedAt)
            .Skip((pg - 1) * sz)
            .Take(sz)
            .Select(r => ToOrderItem(r, 0))
            .ToList();

        return Ok(PagedListResponse<OrderListItem>.Of(window, pg, sz, total));
    }

    private static (int page, int pageSize) NormalizePaging(int? page, int? pageSize)
    {
        var pg = page is > 0 ? page.Value : 1;
        var sz = pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize;
        return (pg, sz);
    }

    private static OrderListItem ToOrderItem(DeliveryRequest r, int offersCount) => new()
    {
        Id = r.Id,
        // No DisplayId on the row; mobile tolerates absence. Short, stable handle derived from the id.
        DisplayId = r.Id.Length > 8 ? r.Id[..8] : r.Id,
        Status = r.Status,
        Title = r.Description,
        Tier = r.TierId,
        OffersCount = offersCount,
        ConversationId = r.ConversationId,
        JeeberId = r.JeeberId,
        Pickup = ToAddressBlock(r.PickupAddress, r.PickupLocation),
        Dropoff = ToAddressBlock(r.DropoffAddress, r.DropoffLocation),
        CreatedAt = r.CreatedAt,
    };

    /// <summary>
    /// iter6 B1 — one available-jobs feed row. Identical to <see cref="ToOrderItem"/> but always
    /// emits pickup/dropoff coordinates: the mobile feed parser (DioRequestFeedRepository
    /// `_parseLocation`) DROPS a request whose pickup or dropoff lacks lat/lng, so the feed must
    /// carry them. `offersCount` is irrelevant on the feed (the jeeber is not the owner) → 0.
    /// </summary>
    private static OrderListItem ToFeedItem(DeliveryRequest r) => ToOrderItem(r, 0);

    /// <summary>Address + optional coordinates. The mobile feed parser needs <c>address</c>,
    /// <c>lat</c> and <c>lng</c> all present to keep a feed row.</summary>
    private static AddressBlock ToAddressBlock(string? address, GeoPoint? loc) => new()
    {
        Address = address,
        Lat = loc?.Lat,
        Lng = loc?.Lng,
    };
}

// ---------------------------------------------------------------------------
// DTOs — paged envelope ({ items, page, pageSize, totalCount, totalPages }) +
// the per-item shape the mobile Orders/Jobs repos parse (id, displayId, title,
// status, tier, offersCount, dropoff.address, pickup.address, conversationId).
// ---------------------------------------------------------------------------

/// <summary>Paged list envelope. Mirrors <c>JeebNotificationsPageResponse</c> — the mobile
/// list repos read the array under <c>items</c>.</summary>
public sealed class PagedListResponse<T>
{
    [JsonPropertyName("items")]
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; init; }

    public static PagedListResponse<T> Of(IReadOnlyList<T> items, int page, int pageSize, int totalCount) => new()
    {
        Items = items,
        Page = page,
        PageSize = pageSize,
        TotalCount = totalCount,
        TotalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0,
    };
}

/// <summary>One Orders/Jobs list row. Field names match the mobile parsers
/// (DioClientHomeRepository / DioOrderRepository): id, displayId, title, status, tier,
/// offersCount, dropoff.address, pickup.address, conversationId.</summary>
public sealed class OrderListItem
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("displayId")]
    public string DisplayId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("tier")]
    public string? Tier { get; init; }

    [JsonPropertyName("offersCount")]
    public int OffersCount { get; init; }

    [JsonPropertyName("conversationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConversationId { get; init; }

    [JsonPropertyName("jeeberId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JeeberId { get; init; }

    [JsonPropertyName("pickup")]
    public AddressBlock Pickup { get; init; } = new();

    [JsonPropertyName("dropoff")]
    public AddressBlock Dropoff { get; init; } = new();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Nested address block the mobile reads as <c>dropoff.address</c> / <c>pickup.address</c>.
/// The available-jobs feed (iter6 B1) also fills <c>lat</c>/<c>lng</c> — the mobile feed parser
/// requires all three to keep a feed row; the Orders/Jobs tabs tolerate their absence.</summary>
public sealed class AddressBlock
{
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; init; }

    [JsonPropertyName("lat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Lat { get; init; }

    [JsonPropertyName("lng")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Lng { get; init; }
}
