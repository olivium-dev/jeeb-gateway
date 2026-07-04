using JeebGateway.Auth.Capabilities;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// GAP-2 (sprint-002, contract-freeze §3) — V1 BFF slice for the jeeber's
/// REQUEST-CENTRIC discovery feed.
///
/// <para><c>GET /v1/jeebers/me/feed</c> returns the <b>pending delivery requests an online
/// jeeber can act on</b> — the in-app read-side of matching. This is the surface that lets a
/// client's request CROSS to a jeeber (sprint-plan Gate B / DoD core-flow Step 2). It is
/// deliberately NOT offer-centric: an offer-centric feed (the jeeber's own already-submitted
/// offers) reproduces the Sprint-001 Step-2 failure — a jeeber who has not yet bid sees an empty
/// feed and can never make a first offer (chicken-and-egg). See contract-freeze §1.</para>
///
/// <para><b>Visibility projection (contract-freeze §1 / §6).</b> A request is visible to jeeber
/// <c>J</c> iff: (1) <c>J</c> is currently online in the delivery-service presence store — the same
/// online-set matching reads; (2) <c>request.status == pending</c>; (3) <c>request.clientId != J</c>
/// (a jeeber never sees their own client requests). Zone/nearby narrowing is a documented
/// fast-follow owned by the matching domain, NOT the gateway.</para>
///
/// <para><b>Thin BFF aggregation.</b> Primary list = the gateway's own request store projected by
/// status (always available). Each item is then ANNOTATED with this jeeber's existing offer
/// (<c>myOffer</c>, nullable) from offer-service <c>GET /api/v1/jeebers/:id/offers</c>, matched by
/// <c>requestId</c>. The gateway runs no auction/matching business logic. DEGRADE-DON'T-FAIL: an
/// offer-service blip degrades <c>myOffer</c> to <c>null</c> rather than failing the feed; an
/// offline jeeber returns an empty feed (200), never an error.</para>
///
/// <para><b>Privacy (contract-freeze §3.4).</b> The feed item strips client PII —
/// <c>clientId</c>, <c>recipientPhone</c>, <c>audioUrl</c>, <c>transcription</c>, <c>photos</c> are
/// NEVER exposed in discovery; they are revealed only post-acceptance on the active-delivery
/// surface.</para>
/// </summary>
[ApiController]
public sealed class JeebFeedController : ControllerBase
{
    private readonly IRequestsStore _requests;
    private readonly IDeliveryServiceClient _delivery;
    private readonly IOfferServiceClient _offerService;
    private readonly UpstreamFeatureFlags _flags;
    private readonly TimeProvider _clock;
    private readonly ILogger<JeebFeedController> _logger;

    public JeebFeedController(
        IRequestsStore requests,
        IDeliveryServiceClient delivery,
        IOfferServiceClient offerService,
        IOptions<UpstreamFeatureFlags> flags,
        TimeProvider clock,
        ILogger<JeebFeedController> logger)
    {
        _requests = requests;
        _delivery = delivery;
        _offerService = offerService;
        _flags = flags.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// GET /v1/jeebers/me/feed?status=pending&amp;limit=50 — the authenticated jeeber's
    /// request-discovery feed (contract-freeze §3). Always 200 on success, including the
    /// offline/empty case (<c>{ items: [], totalCount: 0 }</c>).
    /// </summary>
    [HttpGet("v1/jeebers/me/feed")]
    // ADR-005 L2 §D jeeber-only (contract-freeze §3): a client never reads the jeeber feed.
    [RequireCapability(Capabilities.JeeberFeedRead)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(JeeberFeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFeed(
        [FromQuery] string? status,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var jeeberId, out var problem))
        {
            return problem;
        }

        // (1) Online gate (contract-freeze §1.1/§6.5): the delivery-service presence store is the
        // authority for "who is online" (written by Contract A / availability toggle). Offline OR
        // never-online (upstream null) → empty feed (200), the Gate B negative case. Degrade-safe:
        // a presence read blip is treated as offline rather than 5xx-ing the feed.
        if (!await IsOnlineAsync(jeeberId, ct))
        {
            _logger.LogInformation("jeeber.feed for {JeeberId}: offline → empty feed.", jeeberId);
            return Ok(JeeberFeedResponse.Empty);
        }

        // (2) Primary list: project the gateway request store by status. ListPendingCreatedAtOrBefore
        // is the cross-client pending-request query; filter to the requested status (pending) and
        // exclude the jeeber's OWN client requests (visibility predicate §1.2/§1.3).
        var wanted = string.IsNullOrWhiteSpace(status) ? RequestStatus.Pending : status!.Trim();
        var candidates = await _requests.ListPendingCreatedAtOrBeforeAsync(_clock.GetUtcNow(), ct);

        var visible = candidates
            .Where(r => string.Equals(r.Status, wanted, StringComparison.OrdinalIgnoreCase))
            .Where(r => !string.Equals(r.ClientId, jeeberId, StringComparison.Ordinal))
            // Ordering: oldest-createdAt-first so the longest-waiting request surfaces first
            // (contract-freeze §3.6 — pinned for deterministic QA/mobile assertions).
            .OrderBy(r => r.CreatedAt)
            .ToList();

        if (limit is > 0)
        {
            visible = visible.Take(limit.Value).ToList();
        }

        // (3) Annotation: this jeeber's existing offers, indexed by request_id, to build myOffer.
        // Degrade-don't-fail — an offer-service blip yields no annotations (all myOffer null), the
        // feed still returns its pending requests. Skipped entirely when the Offer upstream is off.
        var offersByRequest = await LoadMyOffersAsync(jeeberId, ct);

        var items = visible
            .Select(r => ToFeedItem(r, offersByRequest))
            .ToList();

        _logger.LogInformation(
            "jeeber.feed for {JeeberId}: {Count} pending request(s) projected (status={Status}).",
            jeeberId, items.Count, wanted);

        return Ok(new JeeberFeedResponse { Items = items, TotalCount = items.Count });
    }

    /// <summary>
    /// Reads the jeeber's presence from the canonical delivery-service store. Null (never online)
    /// or <c>online=false</c> → not online. A read fault degrades to "offline" (privacy-safe,
    /// never 5xx) so a presence blip yields an empty feed instead of an error.
    /// </summary>
    private async Task<bool> IsOnlineAsync(string jeeberId, CancellationToken ct)
    {
        try
        {
            var presence = await _delivery.GetAvailabilityAsync(jeeberId, ct);
            return presence is { Online: true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "jeeber.feed presence read failed for {JeeberId}; treating as offline (empty feed).",
                jeeberId);
            return false;
        }
    }

    /// <summary>
    /// Loads this jeeber's offers and indexes them by request id for the <c>myOffer</c> annotation.
    /// Returns an empty map when the Offer upstream is off or the call degrades — myOffer is then
    /// null for every request (exactly the Gate B fresh-request case).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, JeeberFeedOffer>> LoadMyOffersAsync(
        string jeeberId, CancellationToken ct)
    {
        if (!_flags.Offer)
        {
            return EmptyOffers;
        }

        // status: null → offer-service returns the actionable set (submitted + edited).
        var offers = await _offerService.ListOffersForJeeberAsync(jeeberId, status: null, ct);
        if (offers.Count == 0)
        {
            return EmptyOffers;
        }

        var map = new Dictionary<string, JeeberFeedOffer>(StringComparer.Ordinal);
        foreach (var offer in offers)
        {
            // One offer per (request, jeeber) is enforced upstream (unique index); keep the first
            // if the upstream ever returns dupes so the projection stays deterministic.
            map.TryAdd(offer.RequestId, offer);
        }

        return map;
    }

    private static readonly IReadOnlyDictionary<string, JeeberFeedOffer> EmptyOffers =
        new Dictionary<string, JeeberFeedOffer>(StringComparer.Ordinal);

    private static JeeberFeedItem ToFeedItem(
        DeliveryRequest request,
        IReadOnlyDictionary<string, JeeberFeedOffer> offersByRequest)
    {
        offersByRequest.TryGetValue(request.Id, out var myOffer);

        return new JeeberFeedItem
        {
            RequestId = request.Id,
            Status = request.Status,
            Description = request.Description,
            Pickup = ToFeedLocation(request.PickupAddress, request.PickupLocation),
            Dropoff = ToFeedLocation(request.DropoffAddress, request.DropoffLocation),
            TierId = request.TierId,
            // distanceMeters: jeeber→pickup distance is owned by the matching/geo domain
            // (zone/nearby fast-follow). The gateway does not compute it → null for now.
            DistanceMeters = null,
            CreatedAt = request.CreatedAt,
            MyOffer = myOffer is null
                ? null
                : new FeedMyOffer
                {
                    OfferId = myOffer.OfferId,
                    Status = myOffer.Status,
                    FeeCents = myOffer.FeeCents,
                    EtaMinutes = myOffer.EtaMinutes,
                    Note = myOffer.Note,
                    CreatedAt = myOffer.CreatedAt,
                },
        };
    }

    private static FeedLocation? ToFeedLocation(string? address, GeoPoint? point)
    {
        if (string.IsNullOrWhiteSpace(address) && point is null)
        {
            return null;
        }

        return new FeedLocation
        {
            Address = string.IsNullOrWhiteSpace(address) ? null : address,
            Location = point is null ? null : new FeedGeoPoint { Lat = point.Lat, Lng = point.Lng },
        };
    }
}

/// <summary>GAP-2 — the jeeber feed response envelope (contract-freeze §3.2).</summary>
public sealed class JeeberFeedResponse
{
    public IReadOnlyList<JeeberFeedItem> Items { get; init; } = Array.Empty<JeeberFeedItem>();

    /// <summary>Gate B asserts <c>&gt;= 1</c> for an online jeeber with a pending cross-able request.</summary>
    public int TotalCount { get; init; }

    public static readonly JeeberFeedResponse Empty = new();
}

/// <summary>
/// GAP-2 — one REQUEST-CENTRIC feed item (contract-freeze §3.3). The item key is
/// <see cref="RequestId"/> (the thing to offer on); <see cref="Status"/> is the REQUEST status.
/// Client PII is intentionally absent (§3.4).
/// </summary>
public sealed class JeeberFeedItem
{
    public required string RequestId { get; init; }
    public required string Status { get; init; }
    public required string Description { get; init; }
    public FeedLocation? Pickup { get; init; }
    public FeedLocation? Dropoff { get; init; }
    public string? TierId { get; init; }
    public double? DistanceMeters { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>This jeeber's existing offer on this request, or <c>null</c> if none (the fresh
    /// cross-able request — exactly the Gate B case).</summary>
    public FeedMyOffer? MyOffer { get; init; }
}

/// <summary>GAP-2 — pickup/dropoff summary on a feed item (contract-freeze §3.3 FeedLocation).</summary>
public sealed class FeedLocation
{
    public string? Address { get; init; }
    public FeedGeoPoint? Location { get; init; }
}

/// <summary>GAP-2 — a lat/lng point on a feed location.</summary>
public sealed class FeedGeoPoint
{
    public double Lat { get; init; }
    public double Lng { get; init; }
}

/// <summary>
/// GAP-2 — the jeeber's existing offer annotation on a feed item (contract-freeze §3.3 MyOffer).
/// <see cref="FeeCents"/> stays cents end-to-end (no decimal conversion in the feed — §4.3).
/// </summary>
public sealed class FeedMyOffer
{
    public required string OfferId { get; init; }
    public required string Status { get; init; }
    public long FeeCents { get; init; }
    public int EtaMinutes { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}
