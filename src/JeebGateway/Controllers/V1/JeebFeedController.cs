using JeebGateway.Auth.Capabilities;
using JeebGateway.Geo;
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
/// (a jeeber never sees their own client requests); and (4) the jeeber→pickup great-circle
/// distance is KNOWN and within the request tier's radius (3/10/25 km). Predicate (4) is
/// FAIL-CLOSED — a missing jeeber fix, a missing pickup point, or an unresolvable tier
/// EXCLUDES the request. The "zone/nearby narrowing is a fast-follow owned by the matching
/// domain, NOT the gateway" note that stood here described the state that produced bug D2
/// (a ~9,000 km request offered to a 25 km jeeber, distanceMeters null).</para>
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
///
/// <para><b>G1 sender identity (owner-approved 2026-07-21).</b> A single, deliberately narrow
/// exception to the strip-all-identity stance: each item additionally carries a DISPLAY-SAFE
/// sender annotation — <c>senderName</c> (given name + last initial, e.g. "Nour K.") and
/// <c>senderAvatarUrl</c> (absolute URL) — resolved from the request's client via the gateway's
/// existing user-profile lookup so a jeeber can see who a request is from before accepting. The
/// raw <c>clientId</c>, phone, and email are still never projected; both fields degrade to
/// <c>null</c> (never a feed error) when the profile does not resolve.</para>
/// </summary>
[ApiController]
public sealed class JeebFeedController : ControllerBase
{
    private readonly IRequestsStore _requests;
    private readonly IDeliveryServiceClient _delivery;
    private readonly IOfferServiceClient _offerService;
    private readonly IUsersStore _users;
    private readonly UpstreamFeatureFlags _flags;
    private readonly TimeProvider _clock;
    // P7 (G-H): the offer-wait countdown projection for feed cards.
    private readonly OfferDeadlineProjector _deadlines;
    private readonly GatewayPublicOptions _publicOptions;
    private readonly JeebGateway.Tiers.ITierCatalogResolver _tiers;
    private readonly ILogger<JeebFeedController> _logger;

    public JeebFeedController(
        IRequestsStore requests,
        IDeliveryServiceClient delivery,
        IOfferServiceClient offerService,
        IUsersStore users,
        IOptions<UpstreamFeatureFlags> flags,
        TimeProvider clock,
        OfferDeadlineProjector deadlines,
        IOptions<GatewayPublicOptions> publicOptions,
        JeebGateway.Tiers.ITierCatalogResolver tiers,
        ILogger<JeebFeedController> logger)
    {
        _publicOptions = publicOptions.Value;
        _requests = requests;
        _delivery = delivery;
        _offerService = offerService;
        _users = users;
        _flags = flags.Value;
        _clock = clock;
        _deadlines = deadlines;
        _tiers = tiers;
        _logger = logger;
    }

    /// <summary>
    /// GET /v1/jeebers/me/feed?status=pending&amp;limit=50 — the authenticated jeeber's
    /// request-discovery feed (contract-freeze §3). Always 200 on success, including the
    /// offline/empty case (<c>{ items: [], totalCount: 0 }</c>).
    /// </summary>
    [HttpGet("v1/jeebers/me/feed")]
    // W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
    [HttpGet("jeebers/me/feed")]
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

        // P7 (G-H): ONE clock read for the WHOLE response — the envelope's serverNow,
        // the candidate scan window, and every item's countdown all agree on it.
        var feedNow = _clock.GetUtcNow();

        // (1) Online gate (contract-freeze §1.1/§6.5): the delivery-service presence store is the
        // authority for "who is online" (written by Contract A / availability toggle). Offline OR
        // never-online (upstream null) → empty feed (200), the Gate B negative case. Degrade-safe:
        // a presence read blip is treated as offline rather than 5xx-ing the feed.
        var presence = await ReadPresenceAsync(jeeberId, ct);
        if (presence is not { Online: true })
        {
            _logger.LogInformation("jeeber.feed for {JeeberId}: offline → empty feed.", jeeberId);
            return Ok(JeeberFeedResponse.EmptyAt(feedNow));
        }

        // (2) Primary list: project the gateway request store by status. ListPendingCreatedAtOrBefore
        // is the cross-client pending-request query; filter to the requested status (pending) and
        // exclude the jeeber's OWN client requests (visibility predicate §1.2/§1.3).
        var wanted = string.IsNullOrWhiteSpace(status) ? RequestStatus.Pending : status!.Trim();
        // P7 (G-H): reuse the SAME clock read for the candidate scan so the whole
        // response — envelope serverNow, candidate window, and every item's countdown —
        // is consistent to one instant.
        var candidates = await _requests.ListPendingCreatedAtOrBeforeAsync(feedNow, ct);

        var visible = candidates
            .Where(r => string.Equals(r.Status, wanted, StringComparison.OrdinalIgnoreCase))
            .Where(r => !string.Equals(r.ClientId, jeeberId, StringComparison.Ordinal))
            // Ordering: oldest-createdAt-first so the longest-waiting request surfaces first
            // (contract-freeze §3.6 — pinned for deterministic QA/mobile assertions).
            .OrderBy(r => r.CreatedAt)
            .ToList();

        // (2b) D2 fail-CLOSED tier-radius cut. An unknown distance is an EXCLUSION, never a
        // pass-through: the 9,000 km request that reached a 25 km jeeber got there this way.
        var tierCatalog = await LoadTierCatalogAsync(ct);
        var distancesByRequest = new Dictionary<string, double>(StringComparer.Ordinal);
        var inRadius = new List<DeliveryRequest>(visible.Count);

        foreach (var request in visible)
        {
            var evaluation = TierRadiusPolicy.Evaluate(
                presence.Lat,
                presence.Lng,
                request.PickupLocation,
                tierCatalog.Resolve(request.TierId),
                tierCatalog.IsAvailable);

            if (!evaluation.IsIncluded)
            {
                // radiusKm + distanceMeters on EVERY exclusion: "why" is unactionable without
                // the two numbers the decision was made from.
                _logger.LogInformation(
                    "event={event} jeeberId={JeeberId} requestId={RequestId} tierId={TierId} "
                    + "tierSource={TierSource} catalogRows={CatalogRows} reason={Reason} "
                    + "radiusKm={RadiusKm} distanceMeters={DistanceMeters}",
                    "jeeber.feed.excluded", jeeberId, request.Id, request.TierId,
                    tierCatalog.Source, tierCatalog.Rows.Count,
                    evaluation.Decision, evaluation.RadiusKm, evaluation.DistanceMeters);
                continue;
            }

            distancesByRequest[request.Id] = evaluation.DistanceMeters!.Value;
            inRadius.Add(request);
        }

        visible = inRadius;

        if (limit is > 0)
        {
            visible = visible.Take(limit.Value).ToList();
        }

        // (3) Annotation: this jeeber's existing offers, indexed by request_id, to build myOffer.
        // Degrade-don't-fail — an offer-service blip yields no annotations (all myOffer null), the
        // feed still returns its pending requests. Skipped entirely when the Offer upstream is off.
        var offersByRequest = await LoadMyOffersAsync(jeeberId, ct);

        // (4) G1 (owner-approved 2026-07-21): annotate each item with a DISPLAY-SAFE sender identity
        // (short name + avatar) resolved from the request's client via the gateway's EXISTING
        // user-profile lookup (IUsersStore — the same source the post-acceptance jeeberName reveal
        // uses). Batched over the DISTINCT client ids on this page (cache-per-request). Degrade-don't-
        // fail: a lookup blip yields null sender fields, never a feed failure. The raw clientId / phone
        // / email are NEVER projected — only the short display form + an absolute-https avatar.
        var sendersByClient = await ResolveSendersAsync(visible, ct);

        // P7 (G-H): resolve the tier-TTL projector ONCE for the whole page.
        // Degrade-don't-fail, like every other feed annotation: a blip yields null
        // deadlines, never a feed failure.
        Func<DeliveryRequest, (DateTimeOffset? At, int? Seconds)> project;
        try
        {
            project = await _deadlines.ProjectorForAsync(feedNow, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jeeber.feed offerDeadline decoration failed; serving null deadlines");
            project = _ => (null, null);
        }

        var items = visible
            .Select(r => ToFeedItem(
                r, offersByRequest, sendersByClient, project(r), distancesByRequest[r.Id],
                tierCatalog.Resolve(r.TierId)))
            .ToList();

        _logger.LogInformation(
            "jeeber.feed for {JeeberId}: {Count} pending request(s) projected (status={Status}).",
            jeeberId, items.Count, wanted);

        return Ok(new JeeberFeedResponse
        {
            Items = items,
            TotalCount = items.Count,
            ServerNow = feedNow,
        });
    }

    /// <summary>
    /// Reads the jeeber's presence from the canonical delivery-service store. Null (never online)
    /// or <c>online=false</c> → not online. A read fault degrades to "offline" (privacy-safe,
    /// never 5xx) so a presence blip yields an empty feed instead of an error.
    /// </summary>
    private async Task<JeeberAvailabilityUpstream?> ReadPresenceAsync(
        string jeeberId, CancellationToken ct)
    {
        try
        {
            return await _delivery.GetAvailabilityAsync(jeeberId, ct);
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
            return null;
        }
    }

    /// <summary>
    /// Tier catalog for this page, read ONCE from the same source <c>GET /v1/tiers</c> serves.
    /// A read fault yields an EMPTY snapshot, which the fail-closed evaluator turns into
    /// unknown-tier exclusions rather than an unbounded feed.
    /// </summary>
    private async Task<JeebGateway.Tiers.TierCatalogSnapshot> LoadTierCatalogAsync(CancellationToken ct)
    {
        try
        {
            return await _tiers.SnapshotAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "jeeber.feed tier catalog read failed; excluding every request.");
            return JeebGateway.Tiers.TierCatalogSnapshot.Empty;
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

    /// <summary>
    /// G1 (owner-approved 2026-07-21). Resolves the DISTINCT client ids on this feed page to a
    /// display-safe sender identity, keyed by client id. Batched — each distinct client is looked
    /// up at most once per request (cache-per-request; <see cref="IUsersStore"/> exposes no batch
    /// API). A client that resolves to neither a short name nor an absolute-https avatar is omitted
    /// (its item then carries null sender fields). Never throws: a lookup blip degrades that client
    /// to null so the feed still serves.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, FeedSenderIdentity>> ResolveSendersAsync(
        IReadOnlyList<DeliveryRequest> requests, CancellationToken ct)
    {
        var map = new Dictionary<string, FeedSenderIdentity>(StringComparer.Ordinal);
        foreach (var clientId in requests
                     .Select(r => r.ClientId)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal))
        {
            var identity = await ResolveOneSenderAsync(clientId, ct);
            if (identity is not null)
            {
                map[clientId] = identity;
            }
        }

        return map;
    }

    /// <summary>
    /// Looks up one client's profile via the gateway's existing user-profile store and projects it
    /// to the DISPLAY-SAFE shape: a short name (given name + last initial) and a loadable avatar
    /// URL projected by <see cref="AvatarUrlResolver"/>. Degrade-don't-fail — a null profile or a lookup fault yields <c>null</c> (no sender
    /// annotation) rather than a feed error. The raw clientId is never returned or echoed.
    /// </summary>
    private async Task<FeedSenderIdentity?> ResolveOneSenderAsync(string clientId, CancellationToken ct)
    {
        try
        {
            var profile = await _users.GetByIdAsync(clientId, ct);
            if (profile is null)
            {
                return null;
            }

            var name = ToShortDisplayName(profile.Name);
            var avatar = AvatarUrlResolver.Absolutize(
                profile.AvatarUrl, clientId, _publicOptions.PublicBaseUrl);
            return name is null && avatar is null ? null : new FeedSenderIdentity(name, avatar);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "jeeber.feed sender-identity resolve failed for client {ClientId}; degrading to null.",
                clientId);
            return null;
        }
    }

    /// <summary>
    /// Reduces a full profile name to the DISPLAY-SAFE short form the privacy gate permits pre-
    /// acceptance: given name + last initial (e.g. "Nour Khaled" → "Nour K."). A single-token name
    /// is returned as-is (already just a given name); blank → <c>null</c>. The last initial is the
    /// first grapheme of the last token, so surrogate pairs / combining marks are not split.
    /// </summary>
    private static string? ToShortDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return parts[0];
        }

        var given = parts[0];
        var initial = System.Globalization.StringInfo.GetNextTextElement(parts[^1], 0);
        return string.IsNullOrEmpty(initial) ? given : $"{given} {initial.ToUpperInvariant()}.";
    }

    /// <summary>G1 — resolved display-safe sender identity for one client (both fields nullable).</summary>
    private sealed record FeedSenderIdentity(string? Name, string? AvatarUrl);

    private static JeeberFeedItem ToFeedItem(
        DeliveryRequest request,
        IReadOnlyDictionary<string, JeeberFeedOffer> offersByRequest,
        IReadOnlyDictionary<string, FeedSenderIdentity> sendersByClient,
        (DateTimeOffset? At, int? Seconds) offerDeadline,
        double distanceMeters,
        JeebGateway.Tiers.DeliveryTier? tier)
    {
        offersByRequest.TryGetValue(request.Id, out var myOffer);
        sendersByClient.TryGetValue(request.ClientId, out var sender);

        return new JeeberFeedItem
        {
            RequestId = request.Id,
            Status = request.Status,
            Description = request.Description,
            Pickup = ToFeedLocation(request.PickupAddress, request.PickupLocation),
            Dropoff = ToFeedLocation(request.DropoffAddress, request.DropoffLocation),
            TierId = request.TierId,
            // D-W1: the display projections the row does not hold — same derivation
            // /v1/requests uses, so a UUID tierId never reaches the chip lexicon.
            Tier = JeebGateway.Tiers.TierDisplay.Slug(tier) ?? request.TierId,
            TierName = tier?.Name,
            // D2: a listed item passed the fail-closed radius cut, so this is never null.
            DistanceMeters = distanceMeters,
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
            // G1 (owner-approved): display-safe sender identity, or null when the client profile
            // did not resolve. Derived from the profile — NEVER the raw clientId.
            SenderName = sender?.Name,
            SenderAvatarUrl = sender?.AvatarUrl,
            // P7 (G-H): DERIVED offer-wait deadline — same arithmetic the sweeper
            // commits on, so a jeeber never sees time on a card the sweeper has
            // already decided to expire.
            OfferDeadlineAt = offerDeadline.At,
            OfferDeadlineInSeconds = offerDeadline.Seconds,
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

    /// <summary>
    /// P7: this response's own UTC clock reading — ONE read per response, stamped at
    /// the envelope level. <c>required</c> is deliberate: an unstamped envelope is a
    /// compile error, which is why the old <c>static readonly Empty</c> singleton was
    /// replaced by <see cref="EmptyAt"/>.
    /// </summary>
    public required DateTimeOffset ServerNow { get; init; }

    /// <summary>The empty feed, stamped with the caller's clock read.</summary>
    public static JeeberFeedResponse EmptyAt(DateTimeOffset now) => new() { ServerNow = now };
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

    /// <summary>
    /// D-W1 — the stable lowercase tier token (<c>flash</c>/<c>express</c>/<c>standard</c>) the
    /// client tier chip matches on, falling back to the raw <see cref="TierId"/> when the tier
    /// did not resolve. Byte-compatible with the <c>tier</c> field on <c>/v1/requests</c>;
    /// <see cref="TierId"/> is unchanged. Since the delivery-service cut-over the id is a UUIDv5,
    /// which no client lexicon can match — so without this the chip could never render.
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>D-W1 — the tier's catalog display name (e.g. "Standard"), or null when unresolved.</summary>
    public string? TierName { get; init; }

    public double? DistanceMeters { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>This jeeber's existing offer on this request, or <c>null</c> if none (the fresh
    /// cross-able request — exactly the Gate B case).</summary>
    public FeedMyOffer? MyOffer { get; init; }

    /// <summary>
    /// G1 (owner-approved 2026-07-21) — DISPLAY-SAFE short form of the requesting client's name so
    /// the jeeber sees WHO a request is from pre-acceptance: given name + last initial (e.g.
    /// "Nour K."). <c>null</c> when the client profile can't be resolved (degrade-don't-fail).
    /// Additive; the raw <c>clientId</c>, phone, and email are still NEVER exposed here (§3.4).
    /// </summary>
    public string? SenderName { get; init; }

    /// <summary>
    /// G1 (owner-approved) — the requesting client's avatar as an ABSOLUTE https URL, or <c>null</c>
    /// when absent, not absolute-https, or the lookup degraded. Additive and safe to ignore.
    /// </summary>
    public string? SenderAvatarUrl { get; init; }

    /// <summary>
    /// P7: absolute UTC instant the offer-wait window closes (createdAt + resolved tier
    /// TTL). Null unless the row is pending/matched. NEVER diff this against the handset
    /// clock — anchor on <see cref="OfferDeadlineInSeconds"/> plus the receive instant.
    /// </summary>
    public DateTimeOffset? OfferDeadlineAt { get; init; }

    /// <summary>
    /// P7: seconds remaining at the envelope's <c>serverNow</c>, clamped at 0. Null
    /// exactly when <see cref="OfferDeadlineAt"/> is null.
    /// </summary>
    public int? OfferDeadlineInSeconds { get; init; }
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
