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
    /// GET /v1/requests?role=client|jeeber&amp;page&amp;pageSize — the caller's OWN delivery
    /// requests (Orders tab). <c>role=client</c> (default) lists the requests this user created;
    /// <c>role=jeeber</c> lists the requests this user is the assigned jeeber on (assigned jobs).
    /// Identity is ALWAYS the bearer (never a body/query param). Always 200 with a paged envelope
    /// (empty items when the user has none / on a store blip) — NEVER 405/5xx.
    /// </summary>
    [HttpGet("v1/requests", Order = 2)]
    [RequireCapability(Caps.DeliveryParticipate)] // coarse {client, jeeber}; ownership scoped in-handler
    [ProducesResponseType(typeof(PagedListResponse<OrderListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListRequests(
        [FromQuery] string? role,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        var jeeberScope = string.Equals(role, "jeeber", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<DeliveryRequest> rows;
        try
        {
            if (jeeberScope)
            {
                // Jeeber scope: requests where THIS user is the assigned jeeber. P0 fix — sourced from
                // ListForJeeberAsync (WHERE JeeberId == caller). ListForClientAsync only returns
                // client-owned rows, so a jeeber's accepted deliveries (ClientId = the requesting
                // client, not the jeeber) were never in that set and this branch was always empty.
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

        // PR-G1: the jeeber Jobs surface shows only in-flight assigned jobs — terminal
        // (Done/Cancelled) and Expired rows drop out. The client history surface
        // (role=client) is intentionally UNFILTERED: it is order history and must show
        // terminal rows too (they are still canonical-status'd via ToOrderItem).
        if (jeeberScope)
        {
            rows = rows.Where(IsListableActive).ToList();
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
                var offers = await _offers.ListForRequestAsync(r.Id, ct);
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
    /// GET /v1/deliveries?status&amp;page&amp;pageSize — the caller's OWN assigned deliveries.
    /// A delivery is an accepted request stamped with this user's JeeberId. Identity ALWAYS from the
    /// bearer. Always 200 with a paged envelope (empty when none / on a store blip) — NEVER 404/5xx.
    ///
    /// <para>PR-G1 / FIX-2: <c>status</c> selects the bucket the mobile tab wants (see
    /// <see cref="MatchesBucket"/>). Absent/<c>active</c> = the in-flight Jobs tab (default, unchanged —
    /// terminal/Expired excluded so the active list + BR-10 slot accounting are untouched);
    /// <c>completed|delivered|done</c> = the jeeber's Completed tab (canonical <c>Done</c> rows);
    /// <c>cancelled</c> = cancelled rows. The token vocabulary mirrors the shipped mobile
    /// convention (<c>DioOrderRepository</c> sends <c>active|delivered|cancelled</c>).</para>
    /// </summary>
    [HttpGet("v1/deliveries")]
    [RequireCapability(Caps.DeliveryParticipate)] // {client, jeeber}
    [ProducesResponseType(typeof(PagedListResponse<OrderListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListDeliveries(
        [FromQuery] string? status,
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
            // client are their own created rows that have reached a delivery stage. Union both so the
            // Jobs tab shows the caller's deliveries from whichever side they participate.
            //
            // P0 fix: the jeeber-assigned rows must come from ListForJeeberAsync (WHERE JeeberId ==
            // caller). They CANNOT be recovered from ListForClientAsync — an accepted delivery's
            // ClientId is the requesting client (e.g. Nour), never the jeeber (Karim), so the prior
            // ListForClientAsync(caller) + JeeberId-filter returned nothing for a jeeber and the Jobs
            // tab was always empty. Union the jeeber-assigned rows with the caller's own client rows
            // and de-dupe by id (a user is never both parties on one row, but de-dupe is defensive).
            var assigned = await _requests.ListForJeeberAsync(userId, ct);
            var ownClient = await _requests.ListForClientAsync(userId, ct);
            rows = assigned
                .Concat(ownClient)
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

        // PR-G1 / FIX-2: default (active bucket) lists only in-flight deliveries —
        // canonical-terminal (Done/Cancelled) and Expired excluded so a completed/cancelled
        // job stops occupying the active list (unchanged; BR-10 slot accounting untouched).
        // A ?status= bucket selects the Completed (Done) / Cancelled surface instead. The
        // rows already carry the jeeber's Done row from ListForJeeberAsync (no status
        // predicate at the store), so only this in-handler filter gates which bucket ships.
        // totalCount reflects the filtered set per bucket.
        var listable = rows.Where(r => MatchesBucket(r, status)).ToList();

        var (pg, sz) = NormalizePaging(page, pageSize);
        var total = listable.Count;
        var window = listable
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

    /// <summary>
    /// PR-G1: a row is listable-active when its CANONICAL status is non-terminal and
    /// not the request-lifecycle <c>Expired</c> terminal. Terminal canonical states
    /// (<see cref="CanonicalDeliveryStatus.Done"/> / <see cref="CanonicalDeliveryStatus.Cancelled"/>)
    /// and Expired drop out of the active list surfaces (deliveries / role=jeeber jobs);
    /// <see cref="CanonicalDeliveryStatus.FailedNeedsEscalation"/> is deliberately
    /// non-terminal (admin-resolvable) so it stays visible. Rows whose status has no
    /// canonical delivery mapping (pre-acceptance <c>scheduled/pending/matched</c> and
    /// the holding <c>cancellation_requested</c>) are still in flight, so they list
    /// unless they are a legacy terminal token. The client /v1/requests history surface
    /// is NOT filtered by this — it shows terminal rows too (canonicalized only).
    /// </summary>
    private static bool IsListableActive(DeliveryRequest r)
    {
        var canonical = DeliveryStatusAlias.ToCanonical(r.Status);
        if (canonical is not null)
        {
            return !CanonicalDeliveryStatus.IsTerminal(canonical)
                && !string.Equals(canonical, CanonicalDeliveryStatus.Expired, StringComparison.Ordinal);
        }

        // No canonical delivery mapping (scheduled/pending/matched/cancellation_requested):
        // in flight unless the persisted legacy token is itself terminal.
        return !RequestStatus.IsTerminal(r.Status);
    }

    /// <summary>
    /// FIX-2: maps the optional <c>?status=</c> query token to the delivery bucket the mobile
    /// tab wants. The default (absent / <c>active</c>) is BYTE-IDENTICAL to the prior behaviour —
    /// <see cref="IsListableActive"/> (in-flight only; terminal + Expired excluded; BR-10 unaffected).
    /// <c>completed|delivered|done</c> selects the canonical <see cref="CanonicalDeliveryStatus.Done"/>
    /// bucket (the jeeber Completed tab); <c>cancelled|canceled</c> selects
    /// <see cref="CanonicalDeliveryStatus.Cancelled"/>. An unknown token falls back to the safe
    /// active default so a malformed request can never leak terminal rows into the active surface.
    /// </summary>
    private static bool MatchesBucket(DeliveryRequest r, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || status.Equals("active", StringComparison.OrdinalIgnoreCase))
            return IsListableActive(r); // UNCHANGED default (Jobs tab, BR-10 slot accounting)

        var canonical = DeliveryStatusAlias.ToCanonical(r.Status);

        if (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("delivered", StringComparison.OrdinalIgnoreCase)
            || status.Equals("done", StringComparison.OrdinalIgnoreCase))
            return string.Equals(canonical, CanonicalDeliveryStatus.Done, StringComparison.Ordinal);

        if (status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
            return string.Equals(canonical, CanonicalDeliveryStatus.Cancelled, StringComparison.Ordinal);

        return IsListableActive(r); // unknown token → safe default
    }

    private static OrderListItem ToOrderItem(DeliveryRequest r, int offersCount) => new()
    {
        Id = r.Id,
        // No DisplayId on the row; mobile tolerates absence. Short, stable handle derived from the id.
        DisplayId = r.Id.Length > 8 ? r.Id[..8] : r.Id,
        // PR-G1: surface the CANONICAL SM-1 status token (Ordered/Picked/InTransit/AtDoor/
        // Done/…). In-flight rows persisted under the legacy vocabulary (picked_up/
        // heading_off/…) dual-read to canonical so the mobile Orders/Jobs list shows a
        // 'Picked' row as Picked (never picked_up) and vice-versa, consistently. An
        // entirely unknown token falls back to the raw value rather than dropping it.
        Status = DeliveryStatusAlias.ToCanonical(r.Status) ?? r.Status,
        Title = r.Description,
        Tier = r.TierId,
        OffersCount = offersCount,
        ConversationId = r.ConversationId,
        JeeberId = r.JeeberId,
        Pickup = new AddressBlock { Address = r.PickupAddress },
        Dropoff = new AddressBlock { Address = r.DropoffAddress },
        CreatedAt = r.CreatedAt,
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

/// <summary>Nested address block the mobile reads as <c>dropoff.address</c> / <c>pickup.address</c>.</summary>
public sealed class AddressBlock
{
    [JsonPropertyName("address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Address { get; init; }
}
