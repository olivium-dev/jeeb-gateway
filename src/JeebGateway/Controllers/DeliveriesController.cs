using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Financials;
using JeebGateway.Observability;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace JeebGateway.Controllers;

/// <summary>
/// Delivery status state-machine endpoint (T-backend-013, JEEB-31).
///
/// PATCH /deliveries/{id}/status drives a single linear lifecycle:
///   pending → matched → accepted → picked_up → heading_off → delivered → rated
///
/// Side-effects committed per transition:
/// <list type="bullet">
///   <item>Each transition pushes a <see cref="NotificationTrigger.StatusChange"/>
///     to the "other party" (Client → Jeeber once a Jeeber is bound to the
///     row, and Jeeber → Client). Pre-accept transitions notify the Client
///     only because no Jeeber is bound yet.</item>
///   <item><c>picked_up</c> flips <see cref="DeliveryRequest.GpsTrackingActive"/>
///     true so downstream telemetry can start ingesting Jeeber location
///     updates.</item>
///   <item><c>heading_off → delivered</c> requires the OTP previously
///     issued to the Client at accept-time; a missing or mismatched value
///     rejects with 400.</item>
/// </list>
///
/// Anything else — skipping a step, going backwards, leaving a terminal
/// state, supplying an unknown status string — is rejected by the canonical
/// state machine owned by delivery-service (the gateway forwards the verdict).
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("deliveries")]
// ADR-005 L2 §E delivery state-machine / dual-party: class-level coarse CLAIM {client, jeeber}.
// L2 asserts ONLY the coarse participant role; WHICH party + whether the SM transition is legal +
// ownership stay STATE in the owning delivery service (it already returns 403/409 from state).
// Handover-OTP actions override to the handover.otp.read cap (still {client, jeeber}) below.
[RequireCapability(Capabilities.DeliveryParticipate)]
public class DeliveriesController : ControllerBase
{
    private static readonly ActivitySource ActivitySource = new("JeebGateway.Deliveries");
    private readonly IRequestsStore _store;
    // PR-G3: accepted-offer fee lookup (Amount enrich) + jeeber display-name seam.
    private readonly IPendingOffersStore _offers;
    private readonly IUsersStore _users;
    // JEB-56: settlement store for COD platform records (recorded→batched→paid).
    private readonly ISettlementStore _settlementStore;
    private readonly ISettlementService _settlements;
    private readonly IPushNotificationService _push;
    private readonly ICancellationService _cancellations;
    private readonly IAdminEscalationStore _escalations;
    private readonly IOptions<OtpHandoverOptions> _otpOptions;
    private readonly IOptions<JeebGateway.Auth.OtpSignIn.OtpSignInOptions> _otpSignInOptions;
    private readonly IServiceOTPClient _otpClient;
    private readonly IDeliveryServiceClient _deliveryClient;
    // E22/I3 (JEBV4-241): the SOLE chat caller used to auto-close a delivery's
    // conversation on completion (via the consumed chat-service; see
    // CreditJeeberOnCompletionAsync). Degrade-don't-fail — a chat blip never fails
    // a committed completion.
    private readonly IConversationProvisioner _conversations;
    private readonly IDistributedCache _cache;
    private readonly IHandoverCodeStore _handoverCodes;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeliveriesController> _log;

    // T-BE-019 (JEB-55): external-OTP attempt + lockout TTLs. 15 min on
    // both: long enough to cover the handover window, short enough that
    // a stuck lockout self-heals after the courier moves on. Production
    // tuning lives in OtpHandoverOptions when we promote these to config.
    private static readonly TimeSpan ExternalOtpAttemptsTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ExternalOtpLockoutTtl = TimeSpan.FromMinutes(15);

    private static string AttemptsCacheKey(string deliveryId) => $"otp:attempts:{deliveryId}";
    private static string LockoutCacheKey(string deliveryId)  => $"otp:lockout:{deliveryId}";

    /// <summary>
    /// Per-delivery trace/log label for the handover OTP. NON-GUID by design —
    /// used ONLY for observability (span tags + structured logs), never sent to
    /// the shared one-time-password service. See <see cref="ResolveOtpApplicationId"/>.
    /// </summary>
    private static string HandoverOtpTrace(string deliveryId) => $"delivery_handover_{deliveryId}";

    /// <summary>
    /// JEB-1516: resolve the <c>applicationId</c> forwarded to the shared
    /// one-time-password service on SendOTP / ValidateOTP. The shared service
    /// keys its <c>Phone</c> rows by a tenant GUID and does
    /// <c>new Guid(applicationId)</c> internally; the legacy handover code passed
    /// the human-readable <c>delivery_handover_{id}</c> label, which is NOT a
    /// GUID, so the upstream threw a 400 ("Guid should contain 32 digits with 4
    /// dashes") that the gateway surfaced as a 502 — breaking S09 at-door OTP
    /// issue/verify. Coerce to the configured Jeeb tenant GUID
    /// (<c>Auth:Otp:ApplicationId</c>), exactly like
    /// <see cref="JeebGateway.Auth.OtpSignIn.AuthOtpController"/> and
    /// <see cref="OtpController"/>. The <c>delivery_handover_{id}</c> value is
    /// retained for traces/logs only (<see cref="HandoverOtpTrace"/>).
    /// </summary>
    private string ResolveOtpApplicationId()
        => _otpSignInOptions.Value.ApplicationId;

    /// <summary>
    /// Gap G4 (run-24 CHECK C) store-miss fallback: the accept-issued in-app handover
    /// code, echoed on <c>GET /otp</c> ONLY to the delivery's OWN client (owner-scoped
    /// by an identity match against <see cref="DeliveryRequest.ClientId"/>) so a client
    /// that lost its local copy can re-read it. A READ only — it never mints. Returns
    /// null for a jeeber / non-owner caller and when no code is held. NEVER logs the code.
    /// </summary>
    private async Task<string?> ReadOwnerHandoverCodeAsync(
        string callerId, DeliveryRequest delivery, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(callerId)
            || !string.Equals(callerId, delivery.ClientId, StringComparison.Ordinal))
        {
            return null;
        }

        return await _handoverCodes.GetAsync(delivery.Id, ct);
    }

    /// <summary>
    /// F1/F3/F4 (sprint-009 gateway-flow-correctness-audit): the class capability is the
    /// coarse <c>{client, jeeber}</c> role — i.e. ANY client or ANY jeeber on the platform,
    /// not a party to <em>this</em> delivery. The money-terminating handover steps
    /// (OTP verify/trigger, client-unreachable) additionally require the caller to be a
    /// PARTY to the delivery, exactly like the cancel path. Without this, any authenticated
    /// jeeber who learns/observes the handover code (or simply enumerates ids) could complete
    /// or disrupt someone else's delivery. On the upstream compose path
    /// (<c>FeatureFlags:UseUpstream:Delivery</c>) delivery-service enforces the X-Actor party
    /// guard; these gateway checks close the in-memory (production-live) path.
    /// </summary>
    private static bool IsCallerParty(string callerId, DeliveryRequest delivery)
        => !string.IsNullOrEmpty(callerId)
           && (string.Equals(callerId, delivery.ClientId, StringComparison.Ordinal)
               || string.Equals(callerId, delivery.JeeberId, StringComparison.Ordinal));

    private ObjectResult NotAPartyProblem()
        => StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
        {
            Title = "You are not a party to this delivery.",
            Status = StatusCodes.Status403Forbidden,
            Type = "https://jeeb.dev/errors/not-a-party"
        });

    public DeliveriesController(
        IRequestsStore store,
        IPendingOffersStore offers,
        IUsersStore users,
        ISettlementStore settlementStore,
        ISettlementService settlements,
        IPushNotificationService push,
        ICancellationService cancellations,
        IAdminEscalationStore escalations,
        IOptions<OtpHandoverOptions> otpOptions,
        IOptions<JeebGateway.Auth.OtpSignIn.OtpSignInOptions> otpSignInOptions,
        IServiceOTPClient otpClient,
        IDeliveryServiceClient deliveryClient,
        IConversationProvisioner conversations,
        IDistributedCache cache,
        IHandoverCodeStore handoverCodes,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        TimeProvider clock,
        ILogger<DeliveriesController> log)
    {
        _store = store;
        _offers = offers;
        _users = users;
        _settlementStore = settlementStore;
        _settlements = settlements;
        _push = push;
        _cancellations = cancellations;
        _escalations = escalations;
        _otpOptions = otpOptions;
        _otpSignInOptions = otpSignInOptions;
        _otpClient = otpClient;
        _deliveryClient = deliveryClient;
        _conversations = conversations;
        _cache = cache;
        _handoverCodes = handoverCodes;
        _flags = flags;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// GET /deliveries — proxies to the REAL delivery-service DB via
    /// <c>GET /api/v1/shipments</c>. This is the one gateway endpoint that
    /// proves an end-to-end gateway → delivery-service → Postgres round-trip.
    ///
    /// All query parameters are forwarded verbatim to the upstream.
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(ShipmentsListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ListShipments(
        [FromQuery] string? orderId,
        [FromQuery] string? stage,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        try
        {
            // fix/client-visibility (run-22 P0 hardening): the mobile client-home bucket
            // calls this route with the BUCKET alias `stage=active`, which is NOT a
            // canonical delivery stage (Ordered/Picked/InTransit/AtDoor/Done) — forwarded
            // verbatim it matches nothing upstream and the client's active bucket goes
            // permanently blind. Resolve the alias GATEWAY-SIDE: fetch without the stage
            // filter and keep only in-flight rows (canonical non-terminal, mirroring
            // JeebOrdersListController.IsListableActive semantics). Canonical/unknown
            // stage tokens keep forwarding verbatim (no behavior change).
            var activeBucket = string.Equals(stage, "active", StringComparison.OrdinalIgnoreCase);
            var upstreamStage = activeBucket ? null : stage;

            var result = await _deliveryClient.ListShipmentsAsync(orderId, upstreamStage, limit, ct);

            // PR-G3: the upstream shipments feed is NOT caller-scoped — it returns rows
            // across all orders. Intersect it with the caller's OWN order ids (the
            // gateway's request store, keyed by request id == order id) so a caller only
            // ever sees shipments for orders they participate in. fix/client-visibility:
            // the owned set is the UNION of the rows the caller created (client side) and
            // the rows the caller is the assigned jeeber on — symmetric with the
            // /v1/deliveries list — so the assigned jeeber's active work is visible on
            // this legacy surface too. A shipment whose orderId is absent from the set —
            // or whose orderId is null — is dropped (cannot prove participation → fail
            // closed). This is authorization scoping, not a fabricated dataset.
            // JEBV4-280: read the caller's OWN durable owner-list rows (client-created +
            // jeeber-assigned). The jeeber-assigned rows are the accepted deliveries — the
            // offer-accept stamps them (SetJeeberIdAsync → gateway Postgres mirror), the same
            // acceptance that seeds the delivery-service `deliveries` table. We keep the full
            // rows (not just their ids) so they can be surfaced below.
            var ownedClient = await _store.ListForClientAsync(userId, ct);
            var ownedJeeber = await _store.ListForJeeberAsync(userId, ct);

            var ownedOrderIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owned in ownedClient)
            {
                ownedOrderIds.Add(owned.Id);
            }
            foreach (var assigned in ownedJeeber)
            {
                ownedOrderIds.Add(assigned.Id);
            }

            var scoped = result.Shipments
                .Where(s => s.OrderId is { } oid && ownedOrderIds.Contains(oid))
                .Where(s => StageMatchesBucket(s.CurrentStage, stage, activeBucket))
                .ToList();

            // JEBV4-280: the upstream `shipments` store is a SEPARATE workflow store that only
            // holds a stale seed — it never receives the accepted-delivery rows the accept writes
            // to the `deliveries` store. Intersecting that stale feed with the owner-list (above)
            // therefore dropped every accepted delivery, so a jeeber whose canonical Delivery tab
            // reads this legacy GET /deliveries surface saw "no orders yet" forever after an
            // accept. Surface the jeeber's OWN assigned deliveries from the durable owner-list as
            // shipment rows too (keyed by request id == order id), so an accepted delivery appears
            // here exactly as it does on GET /v1/deliveries?role=jeeber — filtered to the same
            // active statuses (Ordered/Picked/InTransit/AtDoor). STRICTLY ADDITIVE and jeeber-only:
            // the upstream scoped rows are unchanged, an owner-list row is added ONLY for an order
            // id the upstream feed did not already return, and only the JEEBER-assigned rows are
            // considered — so a pure customer's response is byte-for-byte identical to before.
            var upstreamOrderIds = new HashSet<string>(
                scoped.Where(s => s.OrderId is not null).Select(s => s.OrderId!),
                StringComparer.Ordinal);

            // JEBV4-280 (regression fix): resolve the mobile Delivery-tab bucket the SAME way
            // JeebOrdersListController.ListDeliveries does (see StageMatchesBucket). The prior code
            // only special-cased the 'active' bucket and treated EVERY other token as an exact
            // canonical stage — so the 'completed' bucket token (which resolves to no canonical
            // stage) matched nothing and a jeeber's Done delivery vanished from the Completed tab
            // (it is also excluded from Active because it is terminal → invisible on BOTH tabs).
            // The active bucket (stage=active / absent) keeps in-flight rows; completed|delivered|
            // done → canonical Done; cancelled|canceled → canonical Cancelled; an explicit
            // canonical stage token keeps its exact (case-insensitive) match.
            var jeeberShipments = ownedJeeber
                .Where(r => !upstreamOrderIds.Contains(r.Id))
                .Select(r => new { Row = r, Stage = DeliveryStatusAlias.ToCanonical(r.Status) ?? r.Status })
                .Where(x => StageMatchesBucket(x.Stage, stage, activeBucket))
                .Select(x => new ShipmentDetailDto
                {
                    Id = x.Row.Id,
                    TenantId = null,
                    OrderId = x.Row.Id,
                    TierId = x.Row.TierId,
                    WorkflowId = null,
                    WorkflowVersion = 0,
                    CurrentStage = x.Stage,
                    StageEnteredAt = x.Row.CreatedAt,
                    CarrierName = null,
                    CarrierTrackingId = null,
                    CreatedAt = x.Row.CreatedAt,
                    UpdatedAt = x.Row.CreatedAt,
                })
                .ToList();

            var merged = scoped.Concat(jeeberShipments).ToList();
            return Ok(new ShipmentsListDto { Shipments = merged, Count = merged.Count });
        }
        catch (Exception ex)
        {
            // iter5 BATCHED-FIX B11 — the installed APK's client-home + otp_handover
            // surfaces read the legacy non-v1 GET /deliveries; an upstream fault here
            // (502 connection-refused, or any non-2xx ApiException that previously
            // escaped as a raw 500) dead-ended those screens. Degrade to an EMPTY
            // ShipmentsListDto 200 so the list surface renders "no active deliveries"
            // instead of breaking — the audit's preferred B11 alternative. This is the
            // canonical EMPTY-list shape, NOT a fabricated dataset: no fake rows are
            // invented, the empty envelope simply reflects "nothing to show right now".
            _log.LogWarning(
                ex,
                "GET /deliveries upstream call faulted ({Type}); degrading to an empty ShipmentsListDto 200 so the list surface does not dead-end.",
                ex.GetType().Name);
            // Explicit clean-empty envelope: 200 {"shipments":[],"count":0} so the mobile
            // Delivery tab renders "no orders yet" and never the "Something went wrong" toast.
            return Ok(new ShipmentsListDto { Shipments = Array.Empty<ShipmentDetailDto>(), Count = 0 });
        }
    }

    /// <summary>
    /// GET /deliveries/{deliveryId} — single-read of one delivery (S15/S09/S13).
    ///
    /// Reads the gateway's state-machine mirror via
    /// <see cref="IRequestsStore.GetAsync"/>. When
    /// <c>FeatureFlags:UseUpstream:Delivery</c> is on the durable store already
    /// reflects the canonical delivery-service row, so no extra upstream hop is
    /// needed — the gateway stays a thin BFF that composes its own store (no
    /// microservice→microservice coupling, no domain logic).
    ///
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 + <see cref="DeliveryRequestDto"/> when the row exists.</item>
    ///   <item>404 for an unknown id — this is the explicit fix for the S13 E5
    ///     quirk where the prior read-by-id surface returned 500 for an unknown
    ///     delivery. Unknown ⇒ 404, never 500.</item>
    ///   <item>401 when no caller identity is present (auth fires pre-routing).</item>
    /// </list>
    ///
    /// L2 §E coarse {client, jeeber} participation claim applies at the class
    /// level; WHICH party may read which row stays STATE in the owning service
    /// when the canonical path is enabled. The gateway does not re-implement
    /// per-row ownership here — it surfaces the composed mirror.
    /// </summary>
    // FT-02: the original relative route GET /deliveries/{id} is retained for
    // backward compat; the absolute route GET /v1/deliveries/{id} satisfies
    // JEB-152 which expected a versioned BFF delivery status endpoint post-JEB-1433.
    [HttpGet("{deliveryId}")]
    [HttpGet("/v1/deliveries/{deliveryId}")]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string deliveryId, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("get_delivery_by_id");
        activity?.SetTag("delivery.id", deliveryId);

        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        // ---- Canonical read-through (FeatureFlags:UseUpstream:Delivery) ----------
        // delivery-service owns the canonical row + its SM-1 status vocab
        // (Ordered/Picked/InTransit/AtDoor/Done). The gateway reads it through so
        // the surfaced $.status is canonical, not the gateway's legacy snake_case
        // mirror. A transport blip degrades to the local mirror so a read never
        // hard-fails; a null (genuinely-missing) canonical row ALSO degrades to the
        // mirror (F8) so a delivery the caller can otherwise see is never a 404
        // dead-end.
        if (_flags.CurrentValue.Delivery)
        {
            try
            {
                var canonical = await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
                if (canonical is not null)
                {
                    var canonicalDto = new DeliveryRequestDto
                    {
                        Id = canonical.DeliveryId,
                        ClientId = canonical.ClientId ?? string.Empty,
                        Status = canonical.Status,
                        Description = string.Empty,
                        TierId = canonical.TierId,
                        JeeberId = canonical.JeeberId,
                        CreatedAt = canonical.CreatedAt
                    };
                    // fix/client-visibility (run-22 P1): the local mirror row carries the
                    // accept-time fee snapshot the enrichment falls back to when the live
                    // offers lookup cannot resolve the accepted offer (jeeber-party reads,
                    // post-terminal offer-state collapse). Best-effort — a mirror miss just
                    // means no snapshot.
                    var mirror = await _store.GetAsync(deliveryId, ct);
                    await EnrichWithOfferAndJeeberAsync(
                        canonicalDto, deliveryId, canonical.JeeberId, mirror?.AcceptedFee, ct);
                    return Ok(canonicalDto);
                }

                // F8 (JEBV4 live-tracking dead-end): the canonical read returned NULL,
                // which is NOT a transport fault — the upstream `deliveries` row is
                // genuinely absent, almost always because the best-effort seed at
                // create/accept never landed upstream. The gateway's OWN mirror still
                // holds the row — the SAME row GET /deliveries lists and the working
                // chat surface resolves — so a hard 404 here is the S921B/A33
                // "Delivery not found" inconsistency that dead-ends the customer's
                // track step. Fall through to the local mirror below (served under the
                // SAME participant scoping the list applies) instead of a permanent 404.
                _log.LogInformation(
                    "Canonical delivery {DeliveryId} missing upstream (null read); falling back to the local mirror so the caller's own delivery is not a 404 dead-end (F8).",
                    deliveryId);
                // fall through to the local mirror below
            }
            catch (HttpRequestException hre)
            {
                _log.LogWarning(hre,
                    "Canonical delivery read-through failed for {DeliveryId}; falling back to the local mirror.",
                    deliveryId);
                // fall through to the local mirror below
            }
        }

        var delivery = await _store.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            // Unknown id is a clean 404 ProblemDetails, not a 500 (S13 E5 fix).
            return NotFound();
        }

        // F8 participant scoping: the local-mirror fallback surfaces a row the
        // canonical read did not authorise, so it MUST carry the same visibility rule
        // the list surfaces (IRequestsStore.ListForClientAsync ∪ ListForJeeberAsync):
        // the caller must be this delivery's client OR its assigned jeeber. A
        // non-party caller gets the same clean 404 an unknown id yields, so the
        // fallback never widens read access into an unscoped read-any-delivery-by-id
        // surface (guards the role-bleed/privacy invariant).
        if (!CallerParticipatesInDelivery(delivery, callerId))
        {
            _log.LogInformation(
                "Delivery {DeliveryId} mirror read denied: caller is neither the client nor the assigned jeeber; returning 404 (F8 scoping).",
                deliveryId);
            return NotFound();
        }

        var dto = ToDto(delivery);
        await EnrichWithOfferAndJeeberAsync(dto, deliveryId, delivery.JeeberId, delivery.AcceptedFee, ct);
        return Ok(dto);
    }

    /// <summary>
    /// F8: participant-visibility gate for the local-mirror fallback of
    /// GET /deliveries/{id}. A caller may read the mirror row only when they are the
    /// delivery's client or its assigned jeeber — the exact union the caller-scoped
    /// list surfaces (<see cref="IRequestsStore.ListForClientAsync"/> ∪
    /// <see cref="IRequestsStore.ListForJeeberAsync"/>). This keeps the mirror
    /// fallback from becoming an unscoped read-any-delivery-by-id surface while still
    /// resolving the live-tracking 404 dead-end for a delivery the caller owns.
    /// </summary>
    private static bool CallerParticipatesInDelivery(DeliveryRequest delivery, string callerId)
        => string.Equals(delivery.ClientId, callerId, StringComparison.Ordinal)
           || (!string.IsNullOrWhiteSpace(delivery.JeeberId)
               && string.Equals(delivery.JeeberId, callerId, StringComparison.Ordinal));

    /// <summary>
    /// fix/client-visibility (run-22 P0 hardening): a shipment stage counts as ACTIVE
    /// (in flight) when its canonical resolution is non-terminal and not Expired —
    /// the exact IsListableActive predicate the /v1 Jobs list uses. A stage with no
    /// canonical mapping (e.g. an upstream-internal token like <c>created</c>) is
    /// treated as in flight rather than dropped, mirroring the /v1 semantics.
    /// </summary>
    private static bool IsActiveStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return true;
        }

        var canonical = DeliveryStatusAlias.ToCanonical(stage);
        if (canonical is null)
        {
            return !RequestStatus.IsTerminal(stage);
        }

        return !CanonicalDeliveryStatus.IsTerminal(canonical)
            && !string.Equals(canonical, CanonicalDeliveryStatus.Expired, StringComparison.Ordinal);
    }

    /// <summary>
    /// JEBV4-280 (regression fix): resolve the mobile Delivery-tab <c>stage</c> bucket the SAME way
    /// <see cref="V1.JeebOrdersListController"/>.<c>ListDeliveries</c> (MatchesBucket) resolves its
    /// <c>status</c> bucket, so the jeeber's Active AND Completed (and Cancelled) tabs behave
    /// identically on this legacy <c>GET /deliveries</c> surface.
    ///
    /// <list type="bullet">
    ///   <item><c>active</c> / absent → in-flight only (<see cref="IsActiveStage"/>) — UNCHANGED,
    ///     preserving the proven client-home path byte-for-byte.</item>
    ///   <item><c>completed|delivered|done</c> → canonical <see cref="CanonicalDeliveryStatus.Done"/>
    ///     (the jeeber Completed tab). This is the regressed case: the prior code treated
    ///     <c>completed</c> as an exact canonical stage token, which resolves to NOTHING, so a Done
    ///     delivery matched neither the active bucket (terminal) nor the "completed" token and
    ///     disappeared from BOTH tabs.</item>
    ///   <item><c>cancelled|canceled</c> → canonical <see cref="CanonicalDeliveryStatus.Cancelled"/>.</item>
    ///   <item>an explicit canonical stage token (Ordered/Picked/InTransit/AtDoor/…) keeps its exact
    ///     (case-insensitive) match, preserving the prior behaviour for stage-specific callers.</item>
    /// </list>
    /// </summary>
    private static bool StageMatchesBucket(string? rowStage, string? requestedStage, bool activeBucket)
    {
        if (activeBucket || string.IsNullOrWhiteSpace(requestedStage))
        {
            return IsActiveStage(rowStage);
        }

        var rowCanonical = DeliveryStatusAlias.ToCanonical(rowStage) ?? rowStage;

        if (requestedStage.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || requestedStage.Equals("delivered", StringComparison.OrdinalIgnoreCase)
            || requestedStage.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rowCanonical, CanonicalDeliveryStatus.Done, StringComparison.OrdinalIgnoreCase);
        }

        if (requestedStage.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || requestedStage.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rowCanonical, CanonicalDeliveryStatus.Cancelled, StringComparison.OrdinalIgnoreCase);
        }

        var wanted = DeliveryStatusAlias.ToCanonical(requestedStage) ?? requestedStage;
        return string.Equals(rowCanonical, wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PR-G3 (S09): best-effort enrichment of a single-read <see cref="DeliveryRequestDto"/>
    /// with the accepted offer's fee (<see cref="DeliveryRequestDto.Amount"/>) and the
    /// assigned jeeber's display name (<see cref="DeliveryRequestDto.JeeberName"/>).
    /// Both are ADDITIVE and nullable — a resolution miss leaves the field null (omitted
    /// from the JSON), and any store fault is swallowed so a read is NEVER turned into a
    /// 5xx by the decoration. deliveryId == requestId, so the accepted offer is resolved
    /// via the pending-offers store keyed by request id; the jeeber name is resolved via
    /// the gateway's own users projection store (a cheap in-process seam, not an added
    /// upstream user-management round-trip).
    /// </summary>
    private async Task EnrichWithOfferAndJeeberAsync(
        DeliveryRequestDto dto, string deliveryId, string? jeeberId, decimal? snapshotFee, CancellationToken ct)
    {
        try
        {
            var offers = await _offers.ListForRequestAsync(deliveryId, ct);
            // The single accepted offer is the awarded bid; its Fee is the agreed amount.
            var accepted = offers.FirstOrDefault(o =>
                string.Equals(o.Status, PendingOfferStatus.Accepted, StringComparison.Ordinal));
            // fix/client-visibility (run-22 P1): once the delivery completes, the
            // upstream offer's terminal state can collapse out of "accepted" (the
            // gateway's three-state mapping folds every non-accept terminal to
            // withdrawn). The awarded bid is still identifiable as the assigned
            // jeeber's offer — match on the winner before giving up.
            if (accepted is null && !string.IsNullOrWhiteSpace(jeeberId))
            {
                accepted = offers
                    .Where(o => string.Equals(o.JeeberId, jeeberId, StringComparison.Ordinal))
                    .OrderByDescending(o => o.CreatedAt)
                    .FirstOrDefault();
            }
            if (accepted is not null)
            {
                dto.Amount = accepted.Fee;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Accepted-offer amount enrich failed for delivery {DeliveryId}; returning without Amount.",
                deliveryId);
        }

        // fix/client-visibility (run-22 P1): the live offers lookup is owner-scoped on
        // the upstream wire (offer-service 403s any non-owner → empty list here), so a
        // JEEBER reading their own delivery — and any post-completion receipt read that
        // no longer matches an accepted-status offer — falls back to the fee snapshot
        // stamped on the row at accept time. Additive and ignore-when-null preserved.
        if (dto.Amount is null && snapshotFee is > 0m)
        {
            dto.Amount = snapshotFee;
        }

        if (!string.IsNullOrWhiteSpace(jeeberId))
        {
            try
            {
                var profile = await _users.GetByIdAsync(jeeberId, ct);
                if (!string.IsNullOrWhiteSpace(profile?.Name))
                {
                    dto.JeeberName = profile.Name;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Jeeber display-name enrich failed for delivery {DeliveryId} jeeber {JeeberId}; "
                    + "returning without JeeberName.",
                    deliveryId, jeeberId);
            }
        }
    }

    [HttpPatch("{deliveryId}/status")]
    // S03 §5.4 contract alias: the mobile app calls the /v1-prefixed form. Additive
    // second template (byte-compatible, no behavior change) so both the relative and
    // the frozen-contract /v1 form resolve to this action instead of 404.
    [HttpPatch("/v1/deliveries/{deliveryId}/status")]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchStatus(
        string deliveryId,
        [FromBody] PatchStatusBody? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        // ---- Canonical SM-1 path (JEB-1479 cut-over) -----------------------------
        // delivery-service owns the delivery state machine. The legacy in-gateway
        // linear state-machine guard and the local-store transition write path
        // were RETIRED in JEB-1479 — this route no longer touches the local store.
        // The gateway is now an unconditional thin BFF: it accepts the canonical
        // request body (the suite drives {trigger:"pickup"}, {to:"Picked"}, and the
        // legacy {status:"in_transit"} alias), maps it onto the canonical
        // POST /api/v1/deliveries/{id}/transition contract, and forwards verbatim —
        // returning the upstream canonical status (Ordered/Picked/InTransit/AtDoor/
        // Done). An illegal edge is rejected by delivery-service with a typed 422.
        //
        // The legacy V2 mobile route stays alive here (deprecated alias, never a
        // 404): the V2→V3 transition-name adapter is CanonicalDeliveryVocab in this
        // gateway, not delivery-service.
        return await PatchStatusViaDeliveryServiceAsync(deliveryId, body, callerId, ct);
    }

    /// <summary>
    /// Canonical SM-1 transition path (the only PATCH /status path since the
    /// JEB-1479 cut-over retired the local linear state machine).
    /// Resolves the canonical target state from the request body (explicit
    /// <c>to</c>, friendly <c>trigger</c> word, or legacy <c>status</c> alias),
    /// derives the party source from the caller role, and forwards to
    /// delivery-service's <c>POST /api/v1/deliveries/{id}/transition</c>. The
    /// upstream verdict is surfaced verbatim:
    /// <list type="bullet">
    ///   <item>200 + the canonical status returned by delivery-service;</item>
    ///   <item>422 <c>transition_not_allowed</c> / <c>otp_required</c> with the
    ///     typed from/to/trigger extension fields;</item>
    ///   <item>403 wrong-party, 404 unknown, 400 malformed — mapped 1:1.</item>
    /// </list>
    /// The gateway does NOT re-validate the edge — delivery-service owns the SM.
    /// </summary>
    private async Task<IActionResult> PatchStatusViaDeliveryServiceAsync(
        string deliveryId,
        PatchStatusBody? body,
        string callerId,
        CancellationToken ct)
    {
        if (body is null || !CanonicalDeliveryVocab.TryResolveTarget(body, out var canonicalTo))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "A canonical target is required (provide one of: to, trigger, status).",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-transition"
            });
        }

        var partySource = CanonicalDeliveryVocab.PartySourceFor(HttpContext);
        var actorRole = CanonicalDeliveryVocab.ActorRoleFor(HttpContext);

        // fix/status-change-push (AUDIT-B #1): this canonical PATCH path is the sole
        // notification composer on live (flag-on), but historically it never emitted
        // the counterparty StatusChange push the retired in-memory VerifyOtp branch
        // did (NotifyOtherPartyAsync at the old line ~1060). Capture the pre-transition
        // row NOW so that, on a committed transition, we can fan the "status updated"
        // push to the opposite party (Client<->Jeeber) with the correct from->to.
        // STRICTLY best-effort: a null/failed read only means the push is skipped, it
        // never blocks or fails the transition.
        DeliveryRequest? preTransitionRow = null;
        try
        {
            preTransitionRow = await _store.GetAsync(deliveryId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Status-change push pre-read failed for delivery {DeliveryId}; the transition still proceeds, only the counterparty push may be skipped.",
                deliveryId);
        }

        try
        {
            var upstream = await _deliveryClient.CanonicalTransitionAsync(
                deliveryId, canonicalTo, partySource, callerId, actorRole, ct);

            // Best-effort mirror so a subsequent GET (legacy fall-through, or a
            // replica that has not read-through yet) is not stale. delivery-service
            // is the canonical writer — this never fails the 200.
            try
            {
                await _store.SetStatusAsync(deliveryId, upstream.Status, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Canonical transition mirror failed for delivery {DeliveryId} (status {Status}); upstream write is authoritative.",
                    deliveryId, upstream.Status);
            }

            // fix/status-change-push (AUDIT-B #1): the transition committed (200 is
            // authoritative) — fan the StatusChange push to the counterparty, mirroring
            // exactly what the retired in-memory VerifyOtp branch did via
            // NotifyOtherPartyAsync(req, previousStatus). We reuse the pre-transition row
            // for the recipients (ClientId/JeeberId) and stamp the fresh upstream status
            // as the new "to". STRICTLY best-effort: NotifyOtherPartyAsync already swallows
            // per-recipient push faults, and this outer guard ensures a push-composer throw
            // can NEVER turn a committed transition into a 5xx.
            if (preTransitionRow is { } notifyRow)
            {
                try
                {
                    var previousStatus = notifyRow.Status;
                    notifyRow.Status = upstream.Status;
                    await NotifyOtherPartyAsync(notifyRow, previousStatus, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Status-change counterparty push failed for delivery {DeliveryId} (to {Status}); the canonical transition already committed (200) and is authoritative.",
                        deliveryId, upstream.Status);
                }
            }

            // JEB (jeeber-earnings-on-complete): completion can also land via the
            // customer's PATCH → Done ("received it"). Credit the jeeber here too,
            // gated on the canonical Done terminal so non-terminal transitions are a
            // no-op. Idempotent with the OTP-verify leg (exactly-once credit).
            if (string.Equals(
                    DeliveryStatusAlias.ToCanonical(upstream.Status),
                    CanonicalDeliveryStatus.Done,
                    StringComparison.Ordinal))
            {
                await CreditJeeberOnCompletionAsync(deliveryId, HttpContext.TraceIdentifier, ct);
            }

            // Surface the canonical row verbatim (status = upstream canonical vocab).
            return Ok(new DeliveryRequestDto
            {
                Id = upstream.DeliveryId,
                ClientId = string.Empty,
                Status = upstream.Status,
                Description = string.Empty,
                CreatedAt = default,
                AcceptedAt = upstream.TransitionedAt
            });
        }
        catch (DeliveryTransitionException dte)
        {
            return MapTransitionException(dte, deliveryId);
        }
        catch (HttpRequestException hre)
        {
            _log.LogError(hre,
                "Canonical transition upstream network failure for delivery {DeliveryId}.",
                deliveryId);
            return Problem(
                title: "Delivery service unavailable.",
                detail: "Unable to reach delivery-service to apply the transition.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Maps a <see cref="DeliveryTransitionException"/> (a non-2xx from the
    /// canonical SM-1 transition endpoint) onto the gateway's RFC 7807 surface,
    /// echoing the typed from/to/trigger extension fields. The gateway forwards
    /// the upstream verdict; it does not re-interpret it.
    /// </summary>
    private IActionResult MapTransitionException(DeliveryTransitionException dte, string deliveryId)
    {
        switch (dte.StatusCode)
        {
            case StatusCodes.Status404NotFound:
                return NotFound();

            case StatusCodes.Status403Forbidden:
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "You are not a party to this delivery.",
                    Detail = dte.Reason,
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/wrong-party"
                });

            case StatusCodes.Status422UnprocessableEntity:
            {
                // transition_not_allowed | otp_required — surface the typed body.
                var isOtp = string.Equals(dte.Reason, "otp_required", StringComparison.Ordinal);
                var problem = new ProblemDetails
                {
                    Title = isOtp
                        ? "OTP is required to complete this transition."
                        : "Invalid status transition.",
                    Detail = dte.Reason,
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Type = isOtp
                        ? "https://jeeb.dev/errors/otp-required"
                        : "https://jeeb.dev/errors/transition-not-allowed"
                };
                if (dte.Reason is { } reason) problem.Extensions["reason"] = reason;
                if (dte.From is { } from) problem.Extensions["from"] = from;
                if (dte.To is { } to) problem.Extensions["to"] = to;
                if (dte.Trigger is { } trig) problem.Extensions["trigger"] = trig;
                return new ObjectResult(problem)
                {
                    StatusCode = StatusCodes.Status422UnprocessableEntity,
                    ContentTypes = { "application/problem+json" }
                };
            }

            case StatusCodes.Status400BadRequest:
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid status transition.",
                    Detail = dte.Reason,
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/invalid-transition"
                });

            default:
                _log.LogError(
                    "Canonical transition unexpected delivery-service status {UpstreamStatus} for delivery {DeliveryId}.",
                    dte.StatusCode, deliveryId);
                return Problem(
                    title: "Delivery transition failed.",
                    detail: "delivery-service returned an unexpected status.",
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// T-backend-024 (JEEB-42): cancellation endpoint.
    ///
    /// Routing depends on caller role and current status:
    /// <list type="bullet">
    ///   <item>Client, status before <c>picked_up</c> → row goes terminal
    ///     to <c>cancelled</c> immediately. No penalty.</item>
    ///   <item>Client, status <c>picked_up</c> or <c>heading_off</c> →
    ///     row parks on <c>cancellation_requested</c>; the admin queue
    ///     is the only path forward (approve / reject).</item>
    ///   <item>Jeeber, status <c>accepted</c> onwards → reason field
    ///     mandatory; on commit the service consults the rolling-7d
    ///     cancellation count for that Jeeber and applies a 24-hour
    ///     no-new-offers restriction when the count hits 3+.</item>
    /// </list>
    ///
    /// Counterparty push fires on every committed cancel so the other
    /// party finds out without polling.
    ///
    /// <para><b>fix/offer-visibility P2 — V1 request-keyed aliases.</b> The mobile
    /// client cancels a request PRE-ACCEPT keyed by <c>requestId</c>, but the V1
    /// surface had no such route (only the frozen legacy <c>DELETE /requests/{id}</c>
    /// and this <c>POST /deliveries/{id}/cancel</c> existed, forcing the client to
    /// resolve a delivery id first). Because the gateway seeds the durable delivery
    /// row with <c>deliveryId == requestId</c>, the request-keyed cancel IS this
    /// action; we register <c>POST /v1/requests/{id}/cancel</c> and
    /// <c>DELETE /v1/requests/{id}</c> as additional templates on the SAME action
    /// (the established pattern of the <c>/v1/deliveries/*</c> read/OTP aliases in
    /// this controller), so the V1 cancel serves byte-identically through the
    /// canonical path: PR-G2 canonical phase-set membership, counterparty push, and
    /// best-effort upstream propagation
    /// (<see cref="TryPropagateCancellationUpstreamAsync"/>). Semantics follow the
    /// existing cancel contract: pre-accept / pre-pickup OWNER cancel commits
    /// immediately (200, frees the BR-9 slot), post-pickup parks on admin approval,
    /// a non-party gets 403, an unknown id 404s, and a terminal row 409s
    /// <c>not-cancellable</c>. The body stays OPTIONAL
    /// (<c>EmptyBodyBehavior.Allow</c>) so a bare <c>DELETE /v1/requests/{id}</c>
    /// with no JSON body binds <c>null</c> instead of 400-ing — reason remains
    /// mandatory only for the driver path, enforced by the service.</para>
    /// </summary>
    [HttpPost("{deliveryId}/cancel")]
    [HttpPost("/v1/requests/{deliveryId}/cancel")]
    [HttpDelete("/v1/requests/{deliveryId}")]
    [ProducesResponseType(typeof(CancelDeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        string deliveryId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CancelDeliveryBody? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        var callerIsClient = UserIdentity.HasRole(HttpContext, Roles.Client);
        var callerIsJeeber = UserIdentity.HasRole(HttpContext, Roles.Jeeber);

        if (!callerIsClient && !callerIsJeeber)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Cancel requires the customer or driver role.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-role"
            });
        }

        var result = await _cancellations.CancelAsync(
            deliveryId, callerId, callerIsClient, callerIsJeeber, body?.Reason, ct);

        switch (result.Outcome)
        {
            case CancellationOutcome.NotFound:
                return NotFound();

            case CancellationOutcome.NotAuthorized:
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "You are not a party to this delivery.",
                    Status = StatusCodes.Status403Forbidden,
                    Type = "https://jeeb.dev/errors/not-a-party"
                });

            case CancellationOutcome.ReasonRequired:
                return BadRequest(new ProblemDetails
                {
                    Title = "Reason is required when a driver cancels a delivery.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/cancellation-reason-required"
                });

            case CancellationOutcome.NotCancellable:
                return Conflict(new ProblemDetails
                {
                    Title = "Delivery cannot be cancelled in its current state.",
                    Detail = $"current status: {result.Request?.Status}",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/not-cancellable"
                });

            case CancellationOutcome.CancelledImmediately:
            case CancellationOutcome.CancelledByJeeber:
            case CancellationOutcome.PendingAdminApproval:
                await NotifyCancellationCounterpartyAsync(result.Request!, result.PreviousStatus!, result.Outcome, ct);

                // PR-G2: when the gateway commit landed the row TERMINALLY cancelled
                // (immediate client cancel / jeeber cancel), best-effort drive the
                // canonical delivery-service row terminal too so the shipments /
                // canonical projections stop showing the delivery as active. The admin-
                // approval outcome is NOT terminal yet (the row parks on
                // cancellation_requested), so it is deliberately excluded here — upstream
                // propagation happens when the admin approves.
                if (result.Outcome != CancellationOutcome.PendingAdminApproval)
                {
                    await TryPropagateCancellationUpstreamAsync(result.Request!, callerId, ct);
                }

                return Ok(new CancelDeliveryResponse
                {
                    DeliveryId = result.Request!.Id,
                    Status = result.Request.Status,
                    PreviousStatus = result.PreviousStatus!,
                    Reason = result.Reason,
                    PendingApproval = result.Outcome == CancellationOutcome.PendingAdminApproval,
                    JeeberRestricted = result.JeeberRestrictionTriggered,
                    RestrictionExpiresAt = result.RestrictionExpiresAt,
                    JeeberCancellationsLast7Days = result.JeeberCancellationsLast7Days
                });

            default:
                return Problem(
                    title: "Unhandled cancellation outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// PR-G2: best-effort propagation of a committed terminal cancel to the canonical
    /// delivery-service row via the existing SM-1 transition contract
    /// (<see cref="IDeliveryServiceClient.CanonicalTransitionAsync"/>, to =
    /// <see cref="CanonicalDeliveryStatus.Cancelled"/>). Gated on the delivery
    /// kill-switch (<c>FeatureFlags:UseUpstream:Delivery</c>) — when the gateway is the
    /// authoritative writer (flag off) there is no canonical row to reconcile. This is
    /// STRICTLY best-effort: the gateway commit already succeeded and the client 200 is
    /// authoritative, so any upstream fault (already-terminal 422, wrong-party 403,
    /// network 502, …) is swallowed with a LogWarning and never turned into a 5xx. A
    /// sweeper / admin reconcile remains the backstop for a missed propagation.
    /// </summary>
    private async Task TryPropagateCancellationUpstreamAsync(
        DeliveryRequest req, string callerId, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Delivery)
        {
            return;
        }

        try
        {
            var partySource = CanonicalDeliveryVocab.PartySourceFor(HttpContext);
            var actorRole = CanonicalDeliveryVocab.ActorRoleFor(HttpContext);
            await _deliveryClient.CanonicalTransitionAsync(
                req.Id, CanonicalDeliveryStatus.Cancelled, partySource, callerId, actorRole, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Cancellation upstream propagation failed for delivery {DeliveryId}; the gateway "
                + "commit is authoritative and the client cancel already succeeded. The canonical "
                + "row may lag until reconciled.",
                req.Id);
        }
    }

    private async Task NotifyCancellationCounterpartyAsync(
        DeliveryRequest req,
        string previousStatus,
        CancellationOutcome outcome,
        CancellationToken ct)
    {
        var recipients = new List<string> { req.ClientId };
        if (!string.IsNullOrEmpty(req.JeeberId))
        {
            recipients.Add(req.JeeberId);
        }

        var data = new Dictionary<string, string>
        {
            ["deliveryId"] = req.Id,
            ["previousStatus"] = previousStatus,
            ["status"] = req.Status,
            ["cancelledBy"] = req.CancelledBy ?? string.Empty,
            ["pendingApproval"] = (outcome == CancellationOutcome.PendingAdminApproval) ? "true" : "false"
        };

        var title = outcome == CancellationOutcome.PendingAdminApproval
            ? "Cancellation requested"
            : "Delivery cancelled";
        var bodyText = outcome == CancellationOutcome.PendingAdminApproval
            ? "The client requested a cancellation. An admin will review."
            : $"Delivery cancelled from {previousStatus}.";

        foreach (var userId in recipients)
        {
            try
            {
                var request = new PushNotificationRequest(
                    UserId: userId,
                    Trigger: NotificationTrigger.StatusChange,
                    Title: title,
                    Body: bodyText,
                    Data: data,
                    IdempotencyKey: $"{req.Id}:{req.Status}:cancel:{userId}");
                await _push.SendAsync(request, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Cancellation push failed for delivery {DeliveryId} user {UserId}",
                    req.Id, userId);
            }
        }
    }

    /// <summary>
    /// Pushes a <see cref="NotificationTrigger.StatusChange"/> to the user
    /// on the opposite side of the delivery. Pre-accept transitions
    /// (pending → matched) have no Jeeber bound yet — those notify the
    /// Client only. Post-accept transitions notify both directions of the
    /// pair when applicable; the canonical "other party" for a Jeeber
    /// action is the Client and vice versa.
    /// </summary>
    // JEBV4-281: the `ct` parameter is deliberately NOT forwarded to the push send —
    // see the fire-and-forget block below (the push MUST outlive the request scope).
    private Task NotifyOtherPartyAsync(
        DeliveryRequest req, string previousStatus, CancellationToken ct, string? pushStatus = null)
    {
        // pushStatus decouples the PUSH-facing status vocabulary from the request
        // read-model (req.Status). Existing callers omit it, so effectiveStatus falls back
        // to req.Status (byte-for-byte identical behavior). The OTP-verify completion path
        // passes the CANONICAL terminal token (Done) so the counterparty completion push
        // carries "Done" while the request read-model stays gateway-local "delivered".
        // See fix/ci-red-delivery-status-clobber (PR #248).
        var effectiveStatus = pushStatus ?? req.Status;

        // The counterparty depends on the transition:
        //   * pending → matched: notify Client (no Jeeber yet).
        //   * everything else:   notify Client and Jeeber both, since the
        //     PATCH could come from either side and the spec says
        //     "notification to the other party". We send to both so the
        //     gateway doesn't need to know who initiated the patch.
        var recipients = new List<string> { req.ClientId };
        if (!string.IsNullOrEmpty(req.JeeberId))
        {
            recipients.Add(req.JeeberId);
        }

        var data = new Dictionary<string, string>
        {
            ["deliveryId"] = req.Id,
            ["previousStatus"] = previousStatus,
            ["status"] = effectiveStatus,
            ["gpsTrackingActive"] = req.GpsTrackingActive ? "true" : "false"
        };

        var title = "Delivery status updated";
        var bodyText = $"Status changed from {previousStatus} to {effectiveStatus}.";

        // Build the per-recipient push requests SYNCHRONOUSLY so every value is captured
        // from `req` now — the caller mutates/returns the row the instant this returns.
        var deliveryId = req.Id;
        var pushRequests = recipients
            .Select(userId => new PushNotificationRequest(
                UserId: userId,
                Trigger: NotificationTrigger.StatusChange,
                Title: title,
                Body: bodyText,
                Data: data,
                IdempotencyKey: $"{deliveryId}:{effectiveStatus}:{userId}"))
            .ToList();

        // JEBV4-281 — FIRE-AND-FORGET. The status-change push MUST NOT block the
        // transition / OTP-verify response. The push pipeline resolves the counterparty
        // channel via remote-user-preferences (192.168.2.50:10067), which is unreachable
        // on the MSI network; each SendAsync then burns ~10-15s of Polly retries. Awaiting
        // it on the request path made every delivery transition + OTP verify time out
        // client-side ("No internet connection") and the UI revert — even though the
        // backend state had already committed. So detach the send loop onto a background
        // task with its OWN short-timeout token (NOT the request `ct`, which is cancelled
        // the instant the response completes) and swallow+log failures as warnings.
        // `_push` (IPushNotificationService) is a DI SINGLETON (Program.cs), so it is safe
        // to use after the request scope ends. The transition/OTP endpoints now return in
        // <1s regardless of push reachability; when the push IS reachable it delivers
        // exactly as before (same request shape + idempotency key).
        _ = Task.Run(async () =>
        {
            using var pushCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var pushRequest in pushRequests)
            {
                try
                {
                    await _push.SendAsync(pushRequest, pushCts.Token);
                }
                catch (Exception ex)
                {
                    // Best-effort; the state transition already committed. Log for
                    // observability, never surface to the (already-returned) caller.
                    _log.LogWarning(ex,
                        "Status-change push failed for delivery {DeliveryId} user {UserId}",
                        deliveryId, pushRequest.UserId);
                }
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// POST /deliveries/{id}/verify-otp (T-backend-015 / JEEB-33).
    ///
    /// Dedicated hand-off OTP verification surface, distinct from the
    /// PATCH /status endpoint's OTP gate so the attempt-counter and
    /// lockout policy can live on this single endpoint.
    ///
    /// <list type="bullet">
    ///   <item>Correct OTP → transition to <see cref="RequestStatus.Delivered"/> and 200.</item>
    ///   <item>Wrong OTP → 400 with the remaining attempt budget.</item>
    ///   <item>N-th wrong OTP (default 3) → 423 Locked, escalation row created.</item>
    ///   <item>Subsequent calls after lockout → 423 Locked (no extra escalation).</item>
    /// </list>
    /// </summary>
    [HttpPost("{deliveryId}/verify-otp")]
    // ADR-005 L2 §E handover OTP (still {client, jeeber}; party/SM = STATE).
    [RequireCapability(Capabilities.HandoverOtpRead)]
    [ProducesResponseType(typeof(OtpVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OtpLockedResponse), StatusCodes.Status423Locked)]
    public async Task<IActionResult> VerifyOtp(
        string deliveryId,
        [FromBody] OtpVerificationRequest? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.OtpCode))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "otpCode is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/otp-required"
            });
        }

        // F1 (flow-correctness-audit): legacy in-memory verify — require party membership
        // before an OTP match can flip the delivery to Delivered (mirrors VerifyHandoverOtp).
        var legacyDelivery = await _store.GetAsync(deliveryId, ct);
        if (legacyDelivery is null) return NotFound();
        if (!IsCallerParty(callerId, legacyDelivery)) return NotAPartyProblem();

        var opts = _otpOptions.Value;
        var now = _clock.GetUtcNow();
        var result = await _store.TryVerifyOtpAsync(deliveryId, body.OtpCode, opts.MaxAttempts, now, ct);

        switch (result.Outcome)
        {
            case OtpVerificationOutcome.NotFound:
                return NotFound();

            case OtpVerificationOutcome.NotInHandoverState:
                return BadRequest(new ProblemDetails
                {
                    Title = "Delivery is not in the OTP handover state.",
                    Detail = $"OTP verification is only allowed when status is '{RequestStatus.HeadingOff}'.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/otp-not-in-handover-state"
                });

            case OtpVerificationOutcome.Mismatch:
                return BadRequest(new ProblemDetails
                {
                    Title = "Supplied OTP does not match.",
                    Detail = $"{result.AttemptsRemaining} attempt(s) remaining.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/otp-mismatch"
                });

            case OtpVerificationOutcome.Locked:
            {
                // First time this row hits the lockout boundary — open
                // the escalation row, then stamp the id back on the
                // delivery so the sweeper / repeated calls don't race a
                // duplicate.
                if (result.JustLockedOut && result.Request is { } req)
                {
                    var escalation = await _escalations.CreateAsync(new AdminEscalation
                    {
                        Id = Guid.NewGuid().ToString(),
                        DeliveryId = req.Id,
                        ClientId = req.ClientId,
                        JeeberId = req.JeeberId,
                        Reason = EscalationReason.OtpLocked,
                        Status = EscalationStatus.Pending,
                        CreatedAt = now,
                        OtpAttemptCount = req.OtpAttemptCount,
                    }, ct);

                    await _store.TrySetEscalationIdAsync(req.Id, escalation.Id, ct);
                    _log.LogWarning(
                        "OTP lockout for delivery {DeliveryId} after {Attempts} attempts — escalation {EscalationId} opened",
                        req.Id, req.OtpAttemptCount, escalation.Id);
                }

                // Re-read so the response carries the escalation id that
                // was written above (the in-memory store returns the
                // same row, so the field is now populated).
                var locked = result.Request!;
                return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
                {
                    EscalationId = locked.OtpEscalationId ?? string.Empty,
                    LockedAt = locked.OtpLockedAt ?? now,
                    Reason = EscalationReason.OtpLocked
                });
            }

            case OtpVerificationOutcome.Verified:
            {
                // Status flipped to 'delivered'. Fan out the status-change
                // push to both parties, mirroring the PATCH /status path.
                var req = result.Request!;
                await NotifyOtherPartyAsync(req, RequestStatus.HeadingOff, ct);

                // FT-07: enqueue a pending-settlement placeholder so the
                // financial pipeline has a record immediately at handover-
                // complete. The Jeeber will fill in the actual goods cost via
                // POST /deliveries/{id}/settle; SettlementService.SettleAsync
                // will atomically replace this placeholder. Uses deliveryId as
                // the natural idempotency key (TryInsertAsync is a no-op when
                // the row already exists).
                await TryEnqueuePendingSettlementAsync(req, ct);

                return Ok(new OtpVerificationResponse
                {
                    Delivery = ToDto(req),
                    AttemptsRemaining = result.AttemptsRemaining,
                    Verified = true
                });
            }

            default:
                // Defensive — every outcome is handled. If a new enum
                // value lands without a controller branch, fail closed.
                return Problem(
                    title: "Unhandled OTP verification outcome.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /deliveries/{id}/client-unreachable (T-backend-015 step 6).
    /// Jeeber-initiated: starts the 15-min unreachable-client timer.
    /// The <c>OtpHandoverSweeper</c> escalates the row once the window
    /// elapses without a successful OTP verification.
    /// </summary>
    [HttpPost("{deliveryId}/client-unreachable")]
    [ProducesResponseType(typeof(DeliveryRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkClientUnreachable(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        // F3 (flow-correctness-audit): only a party to THIS delivery may start the 15-min
        // unreachable-escalation timer — otherwise any authenticated user could enumerate ids
        // and flood the moderation/escalation queue on deliveries they are not part of.
        var target = await _store.GetAsync(deliveryId, ct);
        if (target is null) return NotFound();
        if (!IsCallerParty(callerId, target)) return NotAPartyProblem();

        var row = await _store.MarkClientUnreachableAsync(deliveryId, _clock.GetUtcNow(), ct);
        if (row is null) return NotFound();

        _log.LogInformation(
            "Delivery {DeliveryId} flagged client-unreachable at {At} — 15-min escalation timer started",
            row.Id, row.ClientUnreachableAt);

        return Ok(ToDto(row));
    }

    /// <summary>
    /// GET /v1/deliveries/{id}/otp (T-BE-019 / JEB-55).
    ///
    /// Issues a 4-digit handover OTP via the external one-time-password service.
    /// The upstream <c>applicationId</c> is the configured Jeeb tenant GUID
    /// (<c>Auth:Otp:ApplicationId</c>, see <see cref="ResolveOtpApplicationId"/>);
    /// the per-delivery <c>delivery_handover_{deliveryId}</c> token is a
    /// trace/log label only (JEB-1516).
    /// Only valid when delivery status = <see cref="RequestStatus.AtDoor"/> —
    /// the Jeeber must have physically arrived at the drop-off before an
    /// OTP is dispatched (PR review B1; per AC1).
    /// </summary>
    [HttpGet("{deliveryId}/otp")]
    // S03 §5.4 contract alias: mobile calls /v1/deliveries/{id}/otp. Additive second
    // template, byte-compatible — both forms resolve here instead of 404 on /v1.
    [HttpGet("/v1/deliveries/{deliveryId}/otp")]
    // ADR-005 L2 §E handover OTP trigger (still {client, jeeber}; AtDoor SM state = STATE).
    [RequireCapability(Capabilities.HandoverOtpRead)]
    [ProducesResponseType(typeof(OtpTriggerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TriggerOtp(string deliveryId, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("trigger_delivery_handover_otp");
        activity?.SetTag("delivery.id", deliveryId);
        activity?.SetTag("otp.type", "external");

        var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        // Gap G4 (run-24 CHECK C): capture the caller id — the accept-issued in-app
        // handover code is echoed on this GET ONLY when the caller owns the delivery
        // (owner-scoped store-miss fallback; see ReadOwnerHandoverCodeAsync).
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        // Get delivery and validate status
        var delivery = await _store.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        // ---- T-BE-019 downstream compose path (FeatureFlags:UseUpstream:Delivery) ----
        // When the kill-switch is on, delivery-service owns the durable
        // at_door gate. The gateway calls /otp/issue FIRST so the gate is
        // enforced server-side and is multi-replica safe, THEN performs the
        // SMS round-trip via one-time-password. The raw code never leaves the
        // gateway↔one-time-password hop (AC5). The in-memory path below stays
        // intact as the documented rollback lever.
        if (_flags.CurrentValue.Delivery)
        {
            return await TriggerOtpViaDeliveryServiceAsync(deliveryId, delivery, callerId, correlationId, activity, ct);
        }

        // F4 (flow-correctness-audit): in-memory path — only a party to THIS delivery may
        // trigger the real SMS dispatch to the recipient, preventing SMS-cost abuse and the
        // 404-vs-400 existence oracle over enumerated delivery ids.
        if (!IsCallerParty(callerId, delivery))
        {
            return NotAPartyProblem();
        }

        // PR review B1 (JEB-628): AC1 requires status `at_door` (the
        // handover step), not `heading_off` (the en-route step). Issuing
        // an OTP before the courier has arrived is the wrong UX.
        if (delivery.Status != RequestStatus.AtDoor)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "OTP can only be triggered when delivery status is 'at_door'.",
                Detail = $"Current status: {delivery.Status}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/invalid-otp-trigger-state"
            });
        }

        // BUG B (JEBV4 fix/bugb-otp-phone-contract): recipient phone is OPTIONAL on
        // create (POST /v1/requests) but was REQUIRED here — a contract mismatch that
        // 400'd the completion handover for every phone-less delivery. The in-app
        // handover code (minted at offer-accept, held in _handoverCodes and echoed to
        // the OWNER via ReadOwnerHandoverCodeAsync) is a self-contained channel that
        // does NOT depend on SMS. So a missing recipient phone must NOT block the
        // handover: we simply skip the (Twilio-broken) SMS leg and return the in-app
        // code. Triggered=false signals "no SMS sent". When a phone IS present we still
        // dispatch the SMS exactly as before (PR review B6: from the row, never a
        // placeholder). This decouples the in-app handover OTP from the SMS path.
        if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
        {
            _log.LogInformation(
                "Handover OTP: no recipient phone for delivery {DeliveryId}; skipping SMS, returning in-app handover code only. correlationId {CorrelationId}",
                deliveryId, correlationId);

            activity?.SetTag("otp.triggered", "false");
            activity?.SetTag("otp.channel", "in_app_only");

            return Ok(new OtpTriggerResponse
            {
                DeliveryId = deliveryId,
                Triggered  = false,
                Message    = "In-app handover code only — no recipient phone on file, so no SMS was sent.",
                // Gap G4 store-miss fallback: echo the accept-issued code to the OWNER only.
                Code       = await ReadOwnerHandoverCodeAsync(callerId, delivery, ct)
            });
        }

        var recipientPhone = delivery.RecipientPhone;
        // JEB-1516: GUID sent upstream; delivery_handover_{id} kept for traces only.
        var applicationId  = ResolveOtpApplicationId();
        var handoverTrace  = HandoverOtpTrace(deliveryId);

        try
        {
            await _otpClient.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber   = recipientPhone,
                ApplicationId = applicationId
            }, ct);

            // PR review B5: never log the upstream message body — it may
            // echo OTP-adjacent data. Log only safe metadata.
            _log.LogInformation(
                "Handover OTP triggered for delivery {DeliveryId} with handover {HandoverTrace}, correlationId {CorrelationId}",
                deliveryId, handoverTrace, correlationId);

            activity?.SetTag("otp.triggered", "true");
            activity?.SetTag("otp.application_id", handoverTrace);

            return Ok(new OtpTriggerResponse
            {
                DeliveryId = deliveryId,
                Triggered  = true,
                Message    = "4-digit OTP sent to the delivery recipient.",
                // Gap G4 store-miss fallback: echo the accept-issued code to the OWNER only.
                Code       = await ReadOwnerHandoverCodeAsync(callerId, delivery, ct)
            });
        }
        catch (ApiException apiEx)
        {
            // PR review B5: do NOT pass apiEx (or its Message) to ILogger —
            // ApiException.Message embeds the upstream response body, which
            // may contain the submitted code. Log only StatusCode + a
            // sanitized marker.
            _log.LogWarning(
                "Handover OTP trigger upstream failure for delivery {DeliveryId}: upstream status {UpstreamStatus}, correlationId {CorrelationId}",
                deliveryId, apiEx.StatusCode, correlationId);

            return Problem(
                title:      "Failed to send OTP",
                detail:     "Unable to trigger OTP via the one-time-password service.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            // Non-ApiException: a network/timeout/cancellation failure
            // before we even reached the upstream. Safe to log the type
            // and message — no upstream body is involved.
            _log.LogError(
                "Handover OTP trigger failed for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                deliveryId, ex.GetType().Name, correlationId);

            return Problem(
                title:      "Failed to send OTP",
                detail:     "Unable to trigger OTP via the one-time-password service.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// POST /v1/deliveries/{id}/otp/verify (T-BE-019 / JEB-55).
    ///
    /// Verifies a 4-digit OTP against the external one-time-password service.
    /// On success: delegates the status transition to delivery-service so the
    /// canonical record is authoritative (commission settlement keys off it).
    /// On failure: increments a shared-cache attempt counter; returns 401 on a
    /// plain wrong code (PR review B2 / AC3) and 423 after the third failure,
    /// at which point a real admin-escalation row is created via
    /// <see cref="IAdminEscalationStore"/> (PR review B7).
    /// </summary>
    [HttpPost("{deliveryId}/otp/verify")]
    // S03 §5.4 contract alias: mobile calls /v1/deliveries/{id}/otp/verify (the real
    // device deliver step). Additive second template, byte-compatible — both forms
    // resolve here instead of 404 on the /v1-prefixed form.
    [HttpPost("/v1/deliveries/{deliveryId}/otp/verify")]
    // ADR-005 L2 §E handover OTP verify (still {client, jeeber}; party/SM/lockout = STATE).
    [RequireCapability(Capabilities.HandoverOtpRead)]
    [ProducesResponseType(typeof(OtpHandoverVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(OtpLockedResponse), StatusCodes.Status423Locked)]
    public async Task<IActionResult> VerifyHandoverOtp(
        string deliveryId,
        [FromBody] OtpHandoverVerificationRequest? body,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("verify_delivery_handover_otp");
        activity?.SetTag("delivery.id", deliveryId);
        activity?.SetTag("otp.type", "external");

        var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        // Capture the authenticated caller identity: on the upstream compose path
        // it is forwarded as X-Actor-* so delivery-service can validate + authorise
        // the AtDoor→Done SM transition (its extractActor has no JWKS of its own).
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.Code))
        {
            return BadRequest(new ProblemDetails
            {
                Title  = "Code is required.",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/otp-code-required"
            });
        }

        var delivery = await _store.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        // ---- T-BE-019 downstream compose path (FeatureFlags:UseUpstream:Delivery) ----
        // When the kill-switch is on, the gateway owns ONLY the code-validation
        // hop against one-time-password; the durable attempt counter, 423-lock,
        // at_door gate, AtDoor→Done transition and single-tx settlement all live
        // in delivery-service. The gateway forwards a success boolean and maps
        // delivery-service's 200/401/423/409/404 straight through as RFC 7807.
        // The raw code never leaves the gateway↔one-time-password hop (AC5).
        if (_flags.CurrentValue.Delivery)
        {
            // X-Actor-* identity for the durable AtDoor→Done leg: the canonical
            // party role (client|jeeber|admin|system) + the authenticated caller id.
            var actorRole = CanonicalDeliveryVocab.ActorRoleFor(HttpContext);
            return await VerifyOtpViaDeliveryServiceAsync(
                deliveryId, delivery, body.Code!, callerId, actorRole, correlationId, activity, ct);
        }

        // F1 (flow-correctness-audit, CRITICAL): in-memory (production-live) path — the caller
        // must be a party to THIS delivery before an OTP match can drive it to Delivered and
        // fire settlement. Identity, not just OTP secrecy, gates the money-terminating step.
        if (!IsCallerParty(callerId, delivery))
        {
            return NotAPartyProblem();
        }

        // PR review B1: handover OTP applies at the `at_door` step.
        if (delivery.Status != RequestStatus.AtDoor)
        {
            return BadRequest(new ProblemDetails
            {
                Title  = "OTP verification only allowed when delivery status is 'at_door'.",
                Detail = $"Current status: {delivery.Status}",
                Status = StatusCodes.Status400BadRequest,
                Type   = "https://jeeb.dev/errors/invalid-otp-verification-state"
            });
        }

        // BUG B (fix/bugb-otp-phone-contract): the phone-missing 400 that used to sit
        // here is GONE. The in-app handover code (matched first, below, via
        // _handoverCodes.TryMatchAsync) needs no phone, so blocking verify up-front
        // broke the phone-less handover. The SMS-code fallback (ValidateOTPAsync) DOES
        // need a phone; that leg is now individually guarded below so a phone-less row
        // simply has no SMS channel and a non-matching code fails as a normal wrong
        // code (attempt++/401), never a 400. PR review B6 still holds where a phone is
        // present (the SMS validate uses the row's phone, never a placeholder).

        // PR review B4: cross-replica safe — IDistributedCache (Redis in prod,
        // in-memory in tests) replaces the static Dictionary. Lockout has a
        // TTL so a stuck row self-heals.
        var now = _clock.GetUtcNow();
        // JEBV4-38 (PP-3) — fail-open on a cache-infrastructure fault: a Redis
        // blip must never itself lock a customer out of a money-adjacent
        // handover, mirroring RedisOtpRequestRateLimiter's precedent. Treat an
        // unreadable lockout marker as "not locked"; this is a resilience
        // degrade, not a security bypass — the code-match check further below
        // is unaffected and a wrong code is still rejected independently.
        var existingLockout = await TryReadCacheAsync(LockoutCacheKey(deliveryId), "read_lockout", deliveryId, ct);
        if (existingLockout is not null)
        {
            // Re-surface the prior escalation if it exists, otherwise return
            // the bare lockout marker (the escalation might have been opened
            // by a sweeper or by this controller earlier in the lockout TTL).
            var prior = await _escalations.GetForDeliveryAsync(deliveryId, EscalationReason.OtpLocked, ct);
            return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
            {
                EscalationId = prior?.Id ?? string.Empty,
                LockedAt     = DecodeLockoutTimestamp(existingLockout) ?? now,
                Reason       = EscalationReason.OtpLocked
            });
        }

        var attemptCount = await ReadAttemptCountAsync(deliveryId, ct);
        // JEB-1516: forward the configured tenant GUID, not the non-GUID
        // delivery_handover_{id} label (which made the upstream Guid.Parse throw → 502).
        var applicationId = ResolveOtpApplicationId();

        bool verified;
        int upstreamStatus = 0;

        // Gap G4 (run-24 CHECK C) verify-precedence: when the customer read the code
        // IN-APP (minted at offer-accept), the gateway holds it — match it FIRST
        // (constant-time). A hit is a valid handover code, short-circuiting the
        // one-time-password round-trip; a miss (or no stored code) falls through to
        // the existing SMS-code validation, so the SMS-minted code keeps working
        // unchanged. The downstream transition/settlement below is untouched; a wrong
        // code still 400s. The submitted code is never logged.
        if (await _handoverCodes.TryMatchAsync(deliveryId, body.Code!, ct))
        {
            verified = true;
            activity?.SetTag("otp.match_source", "gateway_minted");
        }
        else if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
        {
            // BUG B: no recipient phone → the SMS-code channel never existed for this
            // delivery, so the in-app code above was the only valid code. A miss here
            // is a wrong/absent code and is handled exactly like a failed SMS verify
            // (attempt++/401 below) — NOT a 400 that blocks the whole handover.
            activity?.SetTag("otp.match_source", "in_app_only_no_sms");
            verified = false;
        }
        else
        {
            activity?.SetTag("otp.match_source", "one_time_password");
            try
            {
                await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
                {
                    PhoneNumber   = delivery.RecipientPhone,
                    Otp           = body.Code,
                    ApplicationId = applicationId
                }, ct);
                verified = true;
            }
            catch (ApiException apiEx)
            {
                // PR review B5: NEVER log apiEx / apiEx.Message — the NSwag
                // ApiException embeds the upstream response body in Message,
                // which may echo the submitted code or other OTP-adjacent data.
                // Log only the upstream HTTP status.
                verified       = false;
                upstreamStatus = apiEx.StatusCode;
            }
            catch (OperationCanceledException)
            {
                // Request was cancelled (caller disconnect / shutdown). Surface
                // 499-equivalent via the framework default by rethrowing.
                throw;
            }
            catch (Exception ex)
            {
                // Network / timeout failure before reaching upstream — safe to
                // log the exception type but NOT the message (defense in depth).
                _log.LogWarning(
                    "Handover OTP verify pre-upstream failure for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                    deliveryId, ex.GetType().Name, correlationId);
                return Problem(
                    title:      "OTP verification failed",
                    detail:     "Unable to reach the one-time-password service.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        if (verified)
        {
            // JEBV4-268: the canonical state-machine writer is the upstream
            // delivery-service, reached via the CANONICAL SM-1 transition
            // (POST api/v1/deliveries/{id}/transition) — the same contract the
            // PATCH-status and cancel-propagation paths already use (:660/:970).
            // The prior forward here PATCHed the RETIRED jeeb/deliveries/{id}/status
            // route, which delivery-service no longer serves, so this in-memory
            // (flag-OFF) path 404/502'd on Done instead of completing. Hand the
            // transition off canonically so commission settlement (T-BE-020) keys
            // off the same record; the local store mirrors the flip only after the
            // upstream call succeeds. X-Actor-* carry the gateway-resolved caller
            // identity delivery-service's extractActor needs (it has no JWKS).
            var partySource = CanonicalDeliveryVocab.PartySourceFor(HttpContext);
            var actorRole   = CanonicalDeliveryVocab.ActorRoleFor(HttpContext);
            try
            {
                await _deliveryClient.CanonicalTransitionAsync(
                    deliveryId, CanonicalDeliveryStatus.Done, partySource, callerId, actorRole, ct);
            }
            catch (DeliveryTransitionException dte)
            {
                _log.LogError(
                    "Upstream canonical transition failed after successful OTP verify for delivery {DeliveryId}: upstream status {UpstreamStatus}, correlationId {CorrelationId}",
                    deliveryId, dte.StatusCode, correlationId);
                return Problem(
                    title:      "OTP verified but status transition failed",
                    detail:     "Please retry; the OTP remains valid.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (HttpRequestException hreq)
            {
                _log.LogError(
                    "Upstream canonical transition network failure after successful OTP verify for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                    deliveryId, hreq.GetType().Name, correlationId);
                return Problem(
                    title:      "OTP verified but status transition failed",
                    detail:     "Please retry; the OTP remains valid.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            // Mirror the canonical transition in the gateway's read-cache so
            // subsequent GETs do not show stale state. The upstream write is
            // already canonical — this is a best-effort local sync.
            await _store.SetStatusAsync(deliveryId, RequestStatus.Delivered, ct);

            // Clear the attempt + lockout markers (no-op if absent). JEBV4-38
            // (PP-3): best-effort — the verify already succeeded and the
            // delivery already transitioned upstream, so a cache-infra fault
            // here must not fail this response; it only means the markers
            // self-heal via their own TTL instead of an explicit clear.
            await TryRemoveCacheAsync(AttemptsCacheKey(deliveryId), "clear_attempts", deliveryId, ct);
            await TryRemoveCacheAsync(LockoutCacheKey(deliveryId), "clear_lockout", deliveryId, ct);

            // JEBV4-83 (F7): invalidate the Gap-G4 in-app handover code now that the
            // handover verified — otherwise the raw code lingers its full 24h TTL as a
            // stale, still-matchable secret. Degrade-don't-fail (self-heals via TTL on a
            // cache-infra fault), so it can never turn this committed 200 into a 5xx.
            await _handoverCodes.InvalidateAsync(deliveryId, ct);

            // FT-07: enqueue the pending-settlement placeholder so the
            // financial pipeline has a record at handover-complete.
            // Re-read the updated row so its status reflects Delivered.
            var deliveredRow = await _store.GetAsync(deliveryId, ct);
            if (deliveredRow is not null)
            {
                await TryEnqueuePendingSettlementAsync(deliveredRow, ct);
            }

            // AC6: emit the canonical handover.verified event. No request
            // body, no exception messages — only the delivery id and a
            // correlation id so on-call can join against APM traces.
            _log.LogInformation(
                "handover.verified deliveryId={DeliveryId} correlationId={CorrelationId} status=delivered",
                deliveryId, correlationId);

            activity?.SetTag("otp.verified", "true");
            activity?.SetTag("delivery.status_transition", "at_door_to_delivered");

            // JEB-56: gateway-side COD settlement enqueue (idempotent on deliveryId).
            // Creates the COD platform settlement intent (cod_state=recorded) immediately
            // after the handover is verified, before the Jeeber declares the cash via
            // POST /deliveries/{id}/settle. GoodsCost=0 here (declared at settle time);
            // the settle endpoint updates commission math in place.
            //
            // TODO(FT-07): when FT-07 (settlement pipeline fix) is closed, replace this
            // intent-only enqueue with the full delivery-service integration trigger so
            // the settled goodsCost is available here from the durable delivery record.
            await EnqueueCodSettlementIntentAsync(deliveryId, delivery.JeeberId ?? string.Empty,
                delivery.ClientId, delivery.TierId, now, ct);

            return Ok(new OtpHandoverVerificationResponse
            {
                DeliveryId = deliveryId,
                Verified   = true,
                Status     = RequestStatus.Delivered,
                Message    = "OTP verified successfully. Delivery completed."
            });
        }

        // ---- wrong-code branch -------------------------------------------------

        attemptCount++;
        await WriteAttemptCountAsync(deliveryId, attemptCount, ct);

        _log.LogWarning(
            "handover.verification_failed deliveryId={DeliveryId} correlationId={CorrelationId} attempt={Attempt}/{Max} upstreamStatus={UpstreamStatus}",
            deliveryId, correlationId, attemptCount, _otpOptions.Value.MaxAttempts, upstreamStatus);
        BusinessOutcomeTelemetry.OtpVerifyFailures.Add(1,
            new KeyValuePair<string, object?>("outcome", "handover_invalid_code"));

        activity?.SetTag("otp.verified", "false");
        activity?.SetTag("otp.attempt_count", attemptCount);

        var maxAttempts = _otpOptions.Value.MaxAttempts;
        if (attemptCount >= maxAttempts)
        {
            // Persist the lockout flag with the timestamp so subsequent
            // requests can read the locked-at moment back. JEBV4-38 (PP-3):
            // this decision (attemptCount >= maxAttempts, computed above) does
            // not depend on the write succeeding, so a cache-infra fault here
            // is best-effort — log and continue to the real admin-escalation
            // row and the 423 response below rather than 500ing. A blip that
            // drops this write only means the lockout does not durably persist
            // across a Redis outage window, not that this request's rejection
            // is skipped.
            await TrySetCacheAsync(
                LockoutCacheKey(deliveryId),
                EncodeLockoutTimestamp(now),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ExternalOtpLockoutTtl },
                "set_lockout", deliveryId, ct);
            await TryRemoveCacheAsync(AttemptsCacheKey(deliveryId), "clear_attempts_on_lockout", deliveryId, ct);

            // PR review B7: real admin escalation row — surfaces in the
            // moderation queue so ops can triage stuck handovers.
            var escalation = await _escalations.CreateAsync(new AdminEscalation
            {
                Id              = Guid.NewGuid().ToString(),
                DeliveryId      = deliveryId,
                ClientId        = delivery.ClientId,
                JeeberId        = delivery.JeeberId,
                Reason          = EscalationReason.OtpLocked,
                Status          = EscalationStatus.Pending,
                CreatedAt       = now,
                OtpAttemptCount = attemptCount,
            }, ct);

            BusinessOutcomeTelemetry.OtpLockouts.Add(1,
                new KeyValuePair<string, object?>("outcome", "handover_lockout"));
            BusinessOutcomeTelemetry.HandoverEscalations.Add(1,
                new KeyValuePair<string, object?>("outcome", "otp_locked"));

            _log.LogWarning(
                "handover.lockout deliveryId={DeliveryId} correlationId={CorrelationId} attempts={Attempts} escalationId={EscalationId}",
                deliveryId, correlationId, attemptCount, escalation.Id);

            activity?.SetTag("otp.locked_out", "true");
            activity?.SetTag("otp.max_attempts_reached", "true");
            activity?.SetTag("otp.escalation_id", escalation.Id);

            return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
            {
                EscalationId = escalation.Id,
                LockedAt     = now,
                Reason       = EscalationReason.OtpLocked
            });
        }

        // PR review B2 / AC3: wrong code is HTTP 401 (Unauthorized), not 400.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new ObjectResult(new ProblemDetails
        {
            Title  = "OTP verification failed.",
            Detail = $"Invalid code. {maxAttempts - attemptCount} attempt(s) remaining.",
            Status = StatusCodes.Status401Unauthorized,
            Type   = "https://jeeb.dev/errors/otp-verification-failed"
        })
        {
            StatusCode  = StatusCodes.Status401Unauthorized,
            ContentTypes = { "application/problem+json" }
        };
    }

    // ---- T-BE-019 downstream compose helpers (FeatureFlags:UseUpstream:Delivery) ----

    /// <summary>
    /// Issue path, flag-on: delivery-service owns the durable at_door gate.
    /// Order is binding — call <c>/otp/issue</c> FIRST so the gate is enforced
    /// server-side, THEN dispatch the SMS via one-time-password. A 409
    /// <c>not_at_door</c> from delivery-service short-circuits before any SMS is
    /// sent and is propagated as RFC 7807.
    /// </summary>
    private async Task<IActionResult> TriggerOtpViaDeliveryServiceAsync(
        string deliveryId,
        DeliveryRequest delivery,
        string callerId,
        string correlationId,
        Activity? activity,
        CancellationToken ct)
    {
        // BUG B (fix/bugb-otp-phone-contract): recipient phone is OPTIONAL at create, so
        // do NOT reject a phone-less delivery here. The at_door gate below still runs
        // (state is enforced server-side regardless), and the in-app handover code
        // (minted at offer-accept) is returned to the OWNER as a phone-independent
        // channel. We only perform the SMS round-trip when a phone is on file (B6: the
        // row's phone, never a placeholder). This keeps the handover working when SMS is
        // unavailable, matching the in-memory (production-live) path above.
        var hasRecipientPhone = !string.IsNullOrWhiteSpace(delivery.RecipientPhone);

        // 1) Durable at_door gate in delivery-service. The raw code never
        //    leaves the gateway, so we forward no code_hash here (the code does
        //    not exist yet — one-time-password mints it on send).
        try
        {
            await _deliveryClient.IssueHandoverOtpAsync(deliveryId, codeHash: null, ct);
        }
        catch (DeliveryHandoverException dhx)
        {
            return MapHandoverException(dhx, deliveryId, correlationId, activity);
        }
        catch (HttpRequestException hreq)
        {
            _log.LogError(
                "Handover OTP issue gate (upstream) network failure for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                deliveryId, hreq.GetType().Name, correlationId);
            return Problem(
                title:      "Failed to send OTP",
                detail:     "Unable to reach delivery-service to gate the OTP issue.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // 2) SMS round-trip — gateway↔one-time-password hop. Reuse the same
        //    one-time-password client + applicationId convention as the legacy
        //    path so the SMS template is identical. Skipped entirely when the
        //    delivery has no recipient phone (in-app handover code only).
        // JEB-1516: GUID sent upstream; delivery_handover_{id} kept for traces only.
        var applicationId = ResolveOtpApplicationId();
        var handoverTrace = HandoverOtpTrace(deliveryId);
        if (hasRecipientPhone)
        {
            try
            {
                await _otpClient.SendOTPAsync(new SendOTPRequestUserID
                {
                    PhoneNumber   = delivery.RecipientPhone,
                    ApplicationId = applicationId
                }, ct);
            }
            catch (ApiException apiEx)
            {
                // Never log apiEx.Message — it embeds the upstream body (B5/AC5).
                _log.LogWarning(
                    "Handover OTP issue (upstream path) one-time-password failure for delivery {DeliveryId}: upstream status {UpstreamStatus}, correlationId {CorrelationId}",
                    deliveryId, apiEx.StatusCode, correlationId);
                return Problem(
                    title:      "Failed to send OTP",
                    detail:     "Unable to trigger OTP via the one-time-password service.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        _log.LogInformation(
            "Handover OTP issued (upstream path) for delivery {DeliveryId} with handover {HandoverTrace}, smsSent={SmsSent}, correlationId {CorrelationId}",
            deliveryId, handoverTrace, hasRecipientPhone, correlationId);

        activity?.SetTag("otp.triggered", hasRecipientPhone ? "true" : "false");
        activity?.SetTag("otp.path", "upstream");
        activity?.SetTag("otp.channel", hasRecipientPhone ? "sms" : "in_app_only");
        activity?.SetTag("otp.application_id", handoverTrace);

        return Ok(new OtpTriggerResponse
        {
            DeliveryId = deliveryId,
            Triggered  = hasRecipientPhone,
            Message    = hasRecipientPhone
                ? "4-digit OTP sent to the delivery recipient."
                : "In-app handover code only — no recipient phone on file, so no SMS was sent.",
            // Gap G4 store-miss fallback: echo the accept-issued code to the OWNER only.
            Code       = await ReadOwnerHandoverCodeAsync(callerId, delivery, ct)
        });
    }

    /// <summary>
    /// Verify path, flag-on: gateway validates the raw code against
    /// one-time-password (success boolean), then hands the durable
    /// attempt-counter / 423-lock / AtDoor→Done / settlement to
    /// delivery-service via <c>/otp/verify {success}</c>. delivery-service's
    /// 200/401/423/409/404 are mapped straight through as RFC 7807. The raw
    /// code never reaches delivery-service (AC5).
    /// </summary>
    private async Task<IActionResult> VerifyOtpViaDeliveryServiceAsync(
        string deliveryId,
        DeliveryRequest delivery,
        string code,
        string actorId,
        string actorRole,
        string correlationId,
        Activity? activity,
        CancellationToken ct)
    {
        // BUG B (fix/bugb-otp-phone-contract): the phone-missing 400 that used to sit
        // here is GONE. The in-app handover code (matched first, below) needs no phone;
        // only the SMS-code fallback does, and that leg is now guarded individually so a
        // phone-less delivery has no SMS channel and a non-matching code fails as a
        // normal wrong code (success=false → delivery-service maps it to 401), never a
        // blanket 400. B6 still holds where a phone is present (SMS validate uses the
        // row's phone, never a placeholder).

        // JEB-1516: forward the configured tenant GUID, not the non-GUID
        // delivery_handover_{id} label (which made the upstream Guid.Parse throw → 502).
        var applicationId = ResolveOtpApplicationId();

        // 1) Code-validation hop. one-time-password returns 2xx for a correct
        //    code; the NSwag client throws ApiException for a wrong/expired
        //    code. We collapse that to a success boolean — the raw code is
        //    discarded here and NEVER forwarded to delivery-service (AC5).
        // Gap G4 (run-24 CHECK C) verify-precedence: when the customer read the code
        // IN-APP (minted at offer-accept), the gateway holds it — match it FIRST
        // (constant-time) and, on a hit, treat as a valid code INSTEAD OF the
        // one-time-password round-trip. On no-stored-code-or-mismatch, fall through to
        // the existing SMS-code validation so the SMS-minted code keeps working. Either
        // way only the success boolean reaches delivery-service (AC5); the raw code
        // never leaves the gateway and is never logged.
        bool success;
        if (await _handoverCodes.TryMatchAsync(deliveryId, code, ct))
        {
            success = true;
            activity?.SetTag("otp.match_source", "gateway_minted");
        }
        else if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
        {
            // BUG B: no recipient phone → no SMS-code channel ever existed, so the in-app
            // code above was the only valid code. A miss is a wrong code (success=false),
            // handled like any failed verify — NOT a 400 that blocks the handover.
            activity?.SetTag("otp.match_source", "in_app_only_no_sms");
            success = false;
        }
        else
        {
            activity?.SetTag("otp.match_source", "one_time_password");
            try
            {
                await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
                {
                    PhoneNumber   = delivery.RecipientPhone,
                    Otp           = code,
                    ApplicationId = applicationId
                }, ct);
                success = true;
            }
            catch (ApiException)
            {
                // Wrong/expired code. Do NOT log apiEx/Message (B5/AC5).
                success = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    "Handover OTP verify (upstream path) pre-validate failure for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                    deliveryId, ex.GetType().Name, correlationId);
                return Problem(
                    title:      "OTP verification failed",
                    detail:     "Unable to reach the one-time-password service.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        activity?.SetTag("otp.path", "upstream");
        activity?.SetTag("otp.code_valid", success ? "true" : "false");

        // 2) Durable gate in delivery-service: it owns the attempt counter,
        //    the 423-lock, the AtDoor→Done transition and single-tx settlement.
        DeliveryHandoverVerifyResult result;
        try
        {
            result = await _deliveryClient.VerifyHandoverOtpAsync(deliveryId, success, actorId, actorRole, ct);
        }
        catch (DeliveryHandoverException dhx)
        {
            // S09 A7 / E9 / BR-OTP-6 (JEB-55) — idempotent re-verify after success.
            // delivery-service's at_door gate (handover/service.go FSM step 3) fires
            // BEFORE the success/runDone path, so a SECOND verify on an already-`Done`
            // delivery is collapsed into the SAME 409 { reason:"not_at_door" } as a
            // genuinely never-at-door delivery — the gateway cannot tell them apart
            // from the 409 alone. The scenario contract (CP-H / N11 / row A7) requires
            // the duplicate verify to SHORT-CIRCUIT on already-`done` and return the
            // REPLAYED 200 { verified:true, status:"Done" } with NO second settlement.
            //
            // So on a 409 we do ONE canonical state read-through: if the delivery is
            // terminally `Done`, this is the A7 replay — return the prior terminal
            // success. We do NOT re-validate the OTP (already discarded above) and we
            // do NOT re-run the SM transition (we never call verify again), so the
            // OTP-used-once law and exactly-once settlement are preserved. Any other
            // 409 (or non-Done state) keeps the existing not_at_door mapping.
            if (dhx.StatusCode == StatusCodes.Status409Conflict)
            {
                var replay = await TryReplayAlreadyDoneHandoverAsync(deliveryId, correlationId, activity, ct);
                if (replay is not null)
                {
                    return replay;
                }
            }

            return MapHandoverException(dhx, deliveryId, correlationId, activity);
        }
        catch (System.Text.Json.JsonException jx)
        {
            // Belt-and-suspenders: delivery-service returned 200 but the body
            // failed to deserialize (e.g. a contract drift). CRITICAL: a 200
            // means the durable AtDoor→Done transition + settlement ALREADY
            // committed upstream — we must NOT let this surface as a bare 500.
            // Map it to a 502 ProblemDetails and log loudly so the on-call can
            // reconcile (the delivery is very likely already Done).
            _log.LogError(
                jx,
                "handover.verify_deserialization_failed deliveryId={DeliveryId} correlationId={CorrelationId} — delivery-service returned 200 but the body did not bind; the AtDoor->Done transition + settlement may have ALREADY committed upstream. Reconcile manually.",
                deliveryId, correlationId);
            activity?.SetTag("otp.verify_deserialization_failed", "true");
            return Problem(
                title:      "OTP verification response could not be read",
                detail:     "delivery-service accepted the handover but its response could not be parsed. The delivery may already be completed; do not retry without reconciling.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException hreq)
        {
            _log.LogError(
                "Handover OTP verify (upstream path) delivery-service network failure for delivery {DeliveryId}: {ExceptionType}, correlationId {CorrelationId}",
                deliveryId, hreq.GetType().Name, correlationId);
            return Problem(
                title:      "OTP verification failed",
                detail:     "Unable to reach delivery-service to complete the handover.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        // 200: delivery-service confirmed verified + transitioned to Done.
        // AC6: emit handover.verified with the delivery id — never the code.
        _log.LogInformation(
            "handover.verified deliveryId={DeliveryId} correlationId={CorrelationId} status={Status}",
            deliveryId, correlationId, result.Status ?? RequestStatus.Delivered);

        // S03 — terminal read-model projection on the FLAG-ON upstream path.
        // The canonical AtDoor→Done transition + settlement already committed in
        // delivery-service above; this mirrors the terminal flip onto the gateway's
        // local request read-model so the client-facing GET /v1/requests/{id} reads
        // `delivered` (not the last PATCH-status value, e.g. AtDoor). deliveryId ==
        // requestId, so this lands on the right request row. This is the same
        // projection the legacy flag-OFF path performs (mirror of the
        // _store.SetStatusAsync(..., Delivered) call in the in-memory branch).
        // Degrade-don't-fail: a committed, verified handover must NEVER be turned
        // into a 5xx by a best-effort local cache write — log and continue.
        //
        // Capture the genuine PRE-completion status (e.g. AtDoor) NOW, before the terminal
        // projection below flips the row. `delivery` is the LIVE store instance (InMemory
        // GetAsync returns it with no defensive copy), so SetStatusAsync mutates
        // delivery.Status in-place; capturing afterwards would read "delivered" and make
        // the completion push's previousStatus meaningless. Captured here it is correct for
        // both the in-memory and durable store paths.
        var preCompletionStatus = delivery.Status;
        try
        {
            await _store.SetStatusAsync(deliveryId, RequestStatus.Delivered, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Post-verify terminal status projection for delivery {DeliveryId} failed; "
                + "the handover stays verified (200), the request read-model may lag until "
                + "reconciled. correlationId {CorrelationId}",
                deliveryId, correlationId);
        }

        // JEBV4-83 (F7): invalidate the Gap-G4 in-app handover code now that
        // delivery-service confirmed the handover — otherwise the raw code lingers its
        // full 24h TTL as a stale, still-matchable secret. Degrade-don't-fail (self-heals
        // via TTL on a cache-infra fault), so it can never turn this committed 200 into a 5xx.
        await _handoverCodes.InvalidateAsync(deliveryId, ct);

        activity?.SetTag("otp.verified", "true");

        // JEB (jeeber-earnings-on-complete): the handover is COMPLETE — credit the
        // assigned jeeber NOW, server-side, using the server-authoritative COD amount
        // from the delivery row (BR-16). This restores the settlement-on-completion
        // that the flag-ON path lost: the flag-OFF in-memory branch enqueues at
        // lines ~1500-1527, but the upstream compose path returned before ever
        // reaching it, so a completed delivery credited nothing. Best-effort +
        // idempotent — a settlement/ledger fault must never fail this canonical 200.
        await CreditJeeberOnCompletionAsync(deliveryId, correlationId, ct);

        // fix/status-change-push (AUDIT-B #1): the handover is COMPLETE — fan the
        // StatusChange->Done push to the counterparty, mirroring the retired in-memory
        // VerifyOtp branch's NotifyOtherPartyAsync(req, HeadingOff) call that the flag-ON
        // compose path lost (so nobody got the "delivery completed" push on live).
        // STRICTLY best-effort: a push fault must NEVER turn a committed, verified
        // handover into a 5xx.
        try
        {
            // COMPLETE fix (fix/ci-red-delivery-status-clobber, PR #248): the read-model
            // and the counterparty push need OPPOSITE vocab from the same field, so
            // decouple them.
            //   * READ-MODEL: already projected to gateway-local "delivered" by
            //     _store.SetStatusAsync(.., Delivered) above (S03). Do NOT re-write
            //     delivery.Status here — `delivery` is the LIVE store instance, so a raw
            //     write bypasses the terminal-state guard and clobbers that projection.
            //     (That raw write was the S03 red the first commit chased; stamping
            //     "delivered" onto it then broke this completion push.)
            //   * PUSH: the StatusChange completion push must carry the CANONICAL terminal
            //     token ("Done") per contract — passed explicitly via pushStatus, leaving
            //     the read-model untouched. read-model=delivered AND push data=Done now
            //     both hold.
            await NotifyOtherPartyAsync(
                delivery, preCompletionStatus, ct,
                pushStatus: result.Status ?? CanonicalDeliveryStatus.Done);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Handover completion counterparty push failed for delivery {DeliveryId}; the handover stays verified (200). correlationId {CorrelationId}",
                deliveryId, correlationId);
        }

        return Ok(new OtpHandoverVerificationResponse
        {
            DeliveryId = result.DeliveryId,
            Verified   = true,
            Status     = result.Status ?? RequestStatus.Delivered,
            Message    = "OTP verified successfully. Delivery completed."
        });
    }

    /// <summary>
    /// JEB (jeeber-earnings-on-complete): fire the SERVER-DRIVEN settlement that
    /// credits the assigned jeeber the moment the delivery reaches the
    /// handover-complete state. Delegates to
    /// <see cref="ISettlementService.SettleOnCompletionAsync"/> — which sources the
    /// COD amount server-authoritatively from the delivery row (BR-16), posts the
    /// wallet <c>cash_settlement</c> credit, and is idempotent/exactly-once so it is
    /// safe to fire from BOTH completion legs (OTP verify + customer PATCH → Done).
    /// Best-effort: every fault is swallowed + logged so a settlement hiccup can
    /// never turn a committed, verified handover into a 5xx (the settlement row is
    /// the gateway system of record and the ledger reconciler replays a missed post).
    /// </summary>
    private async Task CreditJeeberOnCompletionAsync(string deliveryId, string correlationId, CancellationToken ct)
    {
        try
        {
            var result = await _settlements.SettleOnCompletionAsync(deliveryId, ct);
            _log.LogInformation(
                "settlement.on_complete deliveryId={DeliveryId} correlationId={CorrelationId} outcome={Outcome} settlementId={SettlementId} total={Total}",
                deliveryId, correlationId, result.Outcome,
                result.Settlement?.Id ?? "(none)", result.Settlement?.Total ?? 0m);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "settlement.on_complete_failed deliveryId={DeliveryId} correlationId={CorrelationId}; "
                + "the handover stays complete, the jeeber credit will be reconciled/retried.",
                deliveryId, correlationId);
        }

        // E22 / I3 (JEBV4-241, cross-ref JEBV4-217; Q-036): a COMPLETED delivery
        // auto-closes its chat conversation. Fired from the SAME single completion
        // convergence point as the settlement above, so it runs exactly once per
        // completion for BOTH legs (OTP verify → Done AND customer PATCH → Done) —
        // ONE writer, no second call site. The close is routed through the CONSUMED
        // chat-service (channel deactivate) by IConversationProvisioner; the gateway
        // holds no conversation state and writes no store/Firestore seam. STRICTLY
        // best-effort: a chat blip / missing conversation id must NEVER turn a
        // committed, settled completion into a 5xx.
        try
        {
            var row = await _store.GetAsync(deliveryId, ct);
            await _conversations.CloseConversationAsync(row?.ConversationId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "settlement.on_complete conversation auto-close failed deliveryId={DeliveryId} correlationId={CorrelationId}; "
                + "the completion stays committed, the conversation may close on reconcile.",
                deliveryId, correlationId);
        }
    }

    /// <summary>
    /// S09 A7 / E9 / BR-OTP-6 (JEB-55) idempotent-replay probe. Called only after
    /// delivery-service returns a 409 from the verify hop. Reads the canonical
    /// delivery state once; when it is terminally <c>Done</c> this 409 is the
    /// duplicate-verify-after-success case (delivery-service collapses already-`Done`
    /// into the same <c>not_at_door</c> 409 as a never-at-door row, so the gateway
    /// must disambiguate via this read), and we return the REPLAYED 200
    /// <c>{ verified:true, status:"Done" }</c> — the prior terminal success. This is
    /// an idempotent replay, NOT an OTP reuse: the code was already validated +
    /// discarded on the first verify, and no SM transition or settlement is re-run.
    /// Returns <c>null</c> when the delivery is not <c>Done</c> (a genuine 409 the
    /// caller must surface via <see cref="MapHandoverException"/>) or when the
    /// canonical read is unavailable (fail safe to the existing 409 mapping).
    /// </summary>
    private async Task<IActionResult?> TryReplayAlreadyDoneHandoverAsync(
        string deliveryId,
        string correlationId,
        Activity? activity,
        CancellationToken ct)
    {
        DeliveryReadUpstream? canonical;
        try
        {
            canonical = await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The canonical state read failed (network/contract). Do NOT invent a
            // replay — fall back to the existing 409 mapping so behaviour is
            // unchanged when we cannot prove the delivery is Done.
            _log.LogWarning(
                "handover.verify_replay_probe_failed deliveryId={DeliveryId} correlationId={CorrelationId} exceptionType={ExceptionType}",
                deliveryId, correlationId, ex.GetType().Name);
            return null;
        }

        if (canonical is null ||
            !string.Equals(canonical.Status, CanonicalDeliveryStatus.Done, StringComparison.Ordinal))
        {
            // Not terminal Done → a genuine not_at_door 409. Let the normal mapping run.
            return null;
        }

        // Already-Done → A7 idempotent 200 replay. No OTP re-validation, no SM
        // re-transition, no second settlement (BR-OTP-6).
        _log.LogInformation(
            "handover.verify_idempotent_replay deliveryId={DeliveryId} correlationId={CorrelationId} status={Status}",
            deliveryId, correlationId, CanonicalDeliveryStatus.Done);
        activity?.SetTag("otp.idempotent_replay", "true");
        activity?.SetTag("otp.verified", "true");

        return Ok(new OtpHandoverVerificationResponse
        {
            DeliveryId = deliveryId,
            Verified   = true,
            Status     = CanonicalDeliveryStatus.Done,
            Message    = "OTP already verified. Delivery already completed."
        });
    }

    /// <summary>
    /// Maps a <see cref="DeliveryHandoverException"/> (a non-200 from the frozen
    /// delivery-service handover contract) onto the gateway's RFC 7807 surface,
    /// echoing the contract's typed extension fields. The gateway does not
    /// re-interpret the upstream status; it forwards it.
    /// </summary>
    private IActionResult MapHandoverException(
        DeliveryHandoverException dhx,
        string deliveryId,
        string correlationId,
        Activity? activity)
    {
        activity?.SetTag("otp.upstream_status", dhx.StatusCode);
        activity?.SetTag("otp.upstream_reason", dhx.Reason);

        switch (dhx.StatusCode)
        {
            case StatusCodes.Status404NotFound:
                return NotFound();

            case StatusCodes.Status409Conflict:
                // not_at_door — the durable gate rejected the issue/verify.
                return Conflict(new ProblemDetails
                {
                    Title  = "Delivery is not at the door.",
                    Detail = "The handover OTP is only available once the courier has arrived.",
                    Status = StatusCodes.Status409Conflict,
                    Type   = "https://jeeb.dev/errors/not-at-door"
                });

            case StatusCodes.Status403Forbidden:
                // wrong_party — delivery-service's authorise rejected the actor on
                // the AtDoor→Done leg (the X-Actor-* did not match the assigned
                // client/jeeber). Forward the upstream verdict verbatim instead of
                // masking it as a 502; the route is already cap-gated {client,jeeber},
                // so this is defensive (e.g. a caller authed for a delivery they are
                // not a party to).
                _log.LogWarning(
                    "handover.verify_forbidden deliveryId={DeliveryId} correlationId={CorrelationId} reason={Reason}",
                    deliveryId, correlationId, dhx.Reason);
                return new ObjectResult(new ProblemDetails
                {
                    Title  = "Not authorised for this handover.",
                    Detail = "You are not a party to this delivery's handover.",
                    Status = StatusCodes.Status403Forbidden,
                    Type   = "https://jeeb.dev/errors/handover-wrong-party"
                })
                {
                    StatusCode   = StatusCodes.Status403Forbidden,
                    ContentTypes = { "application/problem+json" }
                };

            case StatusCodes.Status401Unauthorized:
            {
                // invalid_code (+ attempts_remaining). Wrong code is 401 (AC3).
                _log.LogWarning(
                    "handover.verification_failed deliveryId={DeliveryId} correlationId={CorrelationId} attemptsRemaining={AttemptsRemaining}",
                    deliveryId, correlationId, dhx.AttemptsRemaining);
                var problem = new ProblemDetails
                {
                    Title  = "OTP verification failed.",
                    Detail = dhx.AttemptsRemaining is { } rem
                        ? $"Invalid code. {rem} attempt(s) remaining."
                        : "Invalid code.",
                    Status = StatusCodes.Status401Unauthorized,
                    Type   = "https://jeeb.dev/errors/otp-verification-failed"
                };
                if (dhx.AttemptsRemaining is { } remaining)
                {
                    problem.Extensions["attemptsRemaining"] = remaining;
                }
                return new ObjectResult(problem)
                {
                    StatusCode   = StatusCodes.Status401Unauthorized,
                    ContentTypes = { "application/problem+json" }
                };
            }

            case StatusCodes.Status423Locked:
            {
                // locked (+ escalation_id). delivery-service auto-opened the
                // FailedNeedsEscalation row; surface its id for the deep-link.
                _log.LogWarning(
                    "handover.lockout deliveryId={DeliveryId} correlationId={CorrelationId} escalationId={EscalationId}",
                    deliveryId, correlationId, dhx.EscalationId);
                activity?.SetTag("otp.locked_out", "true");
                activity?.SetTag("otp.escalation_id", dhx.EscalationId);
                return StatusCode(StatusCodes.Status423Locked, new OtpLockedResponse
                {
                    EscalationId = dhx.EscalationId ?? string.Empty,
                    // Echo the upstream locked_at stamp (the source-of-truth lock
                    // instant) rather than synthesizing the gateway clock; fall
                    // back to the clock only when delivery-service omits it.
                    LockedAt     = dhx.LockedAt ?? _clock.GetUtcNow(),
                    Reason       = EscalationReason.OtpLocked
                });
            }

            default:
                // Any other upstream status (incl. 5xx) is a downstream fault.
                _log.LogError(
                    "Handover OTP (upstream path) unexpected delivery-service status {UpstreamStatus} for delivery {DeliveryId}, correlationId {CorrelationId}",
                    dhx.StatusCode, deliveryId, correlationId);
                return Problem(
                    title:      "Handover OTP failed",
                    detail:     "delivery-service returned an unexpected status.",
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ---- FT-07: settlement pipeline enqueue -----------------------------------

    /// <summary>
    /// Creates a <see cref="SettlementState.PendingSettlement"/> placeholder row
    /// in the settlement store immediately after a successful handover-OTP verification.
    /// Idempotent: <see cref="ISettlementStore.TryInsertAsync"/> is a no-op when a
    /// row for the same delivery id already exists, so a duplicate OTP-verify (the A7
    /// idempotent-replay path) cannot create a second placeholder. The placeholder is
    /// upgraded to a fully-computed settlement row when the Jeeber calls
    /// POST /deliveries/{id}/settle via <see cref="ISettlementStore.ReplacePendingAsync"/>.
    ///
    /// Commission is pre-computed at zero (GoodsCost=0) so the pipeline record is
    /// structurally complete from the moment the window opens; the Jeeber's actual
    /// accepted offer amount replaces these numbers at settle time.
    /// </summary>
    private async Task TryEnqueuePendingSettlementAsync(DeliveryRequest req, CancellationToken ct)
    {
        try
        {
            var tier = CommissionCalculator.ResolveTier(req.TierId);
            var breakdown = CommissionCalculator.Calculate(0m, tier);

            var pending = new Settlement
            {
                Id              = Guid.NewGuid().ToString(),
                DeliveryId      = req.Id,
                ClientId        = req.ClientId,
                JeeberId        = req.JeeberId ?? string.Empty,
                TierId          = req.TierId ?? string.Empty,
                GoodsCost       = breakdown.GoodsCost,
                CommissionTier  = breakdown.Tier,
                CommissionRate  = breakdown.CommissionRate,
                Commission      = breakdown.Commission,
                Insurance       = breakdown.Insurance,
                Total           = breakdown.Total,
                MinimumFeeApplied = breakdown.MinimumFeeApplied,
                Currency        = SettlementService.CurrencyUsd,
                PaymentMethod   = SettlementService.PaymentMethodCash,
                State           = SettlementState.PendingSettlement,
                SettledAt       = _clock.GetUtcNow(),
            };

            var (_, inserted) = await _settlementStore.TryInsertAsync(pending, ct);
            if (inserted)
            {
                _log.LogInformation(
                    "settlement.pending_enqueued deliveryId={DeliveryId} settlementId={SettlementId}",
                    req.Id, pending.Id);
            }
        }
        catch (Exception ex)
        {
            // Settlement enqueue is best-effort at handover time — the delivery
            // status has already transitioned. Log so reconciliation can replay.
            _log.LogWarning(ex,
                "settlement.pending_enqueue_failed deliveryId={DeliveryId}; window still open via settlement intent endpoint",
                req.Id);
        }
    }

    // ---- IDistributedCache helpers for the OTP attempt counter --------------

    private async Task<int> ReadAttemptCountAsync(string deliveryId, CancellationToken ct)
    {
        // JEBV4-38 (PP-3) — fail-open: an unreadable attempt counter degrades
        // to 0 (no known prior attempts) rather than 500ing the verify. This
        // only affects the LOCKOUT bookkeeping, never the code-match check
        // itself, mirroring RedisOtpRequestRateLimiter's fail-open precedent
        // for a best-effort abuse-control counter.
        var bytes = await TryReadCacheAsync(AttemptsCacheKey(deliveryId), "read_attempt_count", deliveryId, ct);
        if (bytes is null || bytes.Length == 0) return 0;
        return int.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private Task WriteAttemptCountAsync(string deliveryId, int count, CancellationToken ct)
        => TrySetCacheAsync(
            AttemptsCacheKey(deliveryId),
            Encoding.UTF8.GetBytes(count.ToString(CultureInfo.InvariantCulture)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ExternalOtpAttemptsTtl },
            "write_attempt_count", deliveryId, ct);

    // ---- JEBV4-38 (PP-3) degrade-don't-fail cache wrappers -------------------
    //
    // The OTP-handover VERIFY path is money-adjacent (it fires COD
    // settlement) and MUST NOT fail-closed on a Redis blip — mirroring
    // RedisOtpRequestRateLimiter.TryAcquire's fail-open precedent for the
    // (lower-stakes) sign-in limiter. These wrappers catch ONLY a
    // cache-infrastructure fault (Redis unreachable/timeout — see
    // IsCacheInfrastructureFault) and degrade:
    //   - a READ returns null/no-op (treated as "no marker" by the caller —
    //     e.g. "not locked" or "0 prior attempts");
    //   - a WRITE is best-effort (logged, swallowed) — the caller's response
    //     to the user does not depend on the write succeeding.
    // This is a resilience gate, not a security gate: nothing here ever turns
    // a fault into an accepted/matched code. TryMatchAsync's own fail-open
    // (DistributedCacheHandoverCodeStore, Requests/OtpHandover) independently
    // falls through to the SMS one-time-password check, which still rejects a
    // wrong code even mid-outage.

    private async Task<byte[]?> TryReadCacheAsync(string cacheKey, string op, string deliveryId, CancellationToken ct)
    {
        try
        {
            return await _cache.GetAsync(cacheKey, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCacheInfrastructureFault(ex))
        {
            _log.LogWarning(ex,
                "handover.cache_fault deliveryId={DeliveryId} op={Op}; failing open",
                deliveryId, op);
            return null;
        }
    }

    private async Task TrySetCacheAsync(
        string cacheKey, byte[] value, DistributedCacheEntryOptions options, string op, string deliveryId, CancellationToken ct)
    {
        try
        {
            await _cache.SetAsync(cacheKey, value, options, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCacheInfrastructureFault(ex))
        {
            _log.LogWarning(ex,
                "handover.cache_fault deliveryId={DeliveryId} op={Op}; best-effort write dropped",
                deliveryId, op);
        }
    }

    private async Task TryRemoveCacheAsync(string cacheKey, string op, string deliveryId, CancellationToken ct)
    {
        try
        {
            await _cache.RemoveAsync(cacheKey, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCacheInfrastructureFault(ex))
        {
            _log.LogWarning(ex,
                "handover.cache_fault deliveryId={DeliveryId} op={Op}; best-effort clear dropped (marker self-heals via TTL)",
                deliveryId, op);
        }
    }

    /// <summary>
    /// JEBV4-38 (PP-3) — recognises a cache-INFRASTRUCTURE fault (Redis
    /// unreachable/timeout), the same catch shape as
    /// <c>RedisOtpRequestRateLimiter.TryAcquire</c>. <see cref="TimeoutException"/>
    /// is included defensively for a client-side timeout surfaced as the BCL
    /// type. The in-memory <see cref="IDistributedCache"/> used in dev/tests
    /// never throws either, so this is a safe no-op outside a real Redis
    /// deployment.
    /// </summary>
    private static bool IsCacheInfrastructureFault(Exception ex) =>
        ex is RedisException or TimeoutException;

    private static byte[] EncodeLockoutTimestamp(DateTimeOffset at)
        => Encoding.UTF8.GetBytes(at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

    private static DateTimeOffset? DecodeLockoutTimestamp(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        return long.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
    }

    private static DeliveryRequestDto ToDto(DeliveryRequest r) => new()
    {
        Id = r.Id,
        ClientId = r.ClientId,
        Status = r.Status,
        Description = r.Description,
        PickupAddress = r.PickupAddress,
        DropoffAddress = r.DropoffAddress,
        CreatedAt = r.CreatedAt,
        ScheduledAt = r.ScheduledAt,
        JeeberId = r.JeeberId,
        AcceptedAt = r.AcceptedAt,
        GpsTrackingActive = r.GpsTrackingActive,
        OtpAttemptCount = r.OtpAttemptCount,
        OtpLockedAt = r.OtpLockedAt,
        ClientUnreachableAt = r.ClientUnreachableAt,
        OtpEscalationId = r.OtpEscalationId
    };

    // JEB-56: COD settlement intent enqueue, called on OTP verify success.
    // Creates a minimal settlement record (cod_state=recorded, goodsCost=0) so
    // the COD batch cron can work idempotently even before the Jeeber declares
    // the cash via POST /deliveries/{id}/settle. Errors are logged but never
    // propagate — the OTP verify 200 must not fail due to settlement write issues.
    private async Task EnqueueCodSettlementIntentAsync(
        string deliveryId,
        string jeeberId,
        string clientId,
        string? tierId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            var existing = await _settlementStore.GetByDeliveryAsync(deliveryId, ct);
            if (existing is not null)
            {
                _log.LogDebug(
                    "COD settlement intent already exists for deliveryId={DeliveryId}; skipping enqueue",
                    deliveryId);
                return;
            }

            var intent = new Settlement
            {
                Id              = Guid.NewGuid().ToString(),
                DeliveryId      = deliveryId,
                JeeberId        = jeeberId,
                ClientId        = clientId,
                TierId          = tierId ?? string.Empty,
                GoodsCost       = 0m,
                CommissionTier  = CommissionCalculator.ResolveTier(tierId),
                CommissionRate  = 0m,
                Commission      = 0m,
                Insurance       = 0m,
                Total           = 0m,
                MinimumFeeApplied = false,
                Currency        = SettlementService.CurrencyUsd,
                PaymentMethod   = SettlementService.PaymentMethodCash,
                State           = SettlementState.PendingSettlement,
                CodState        = CodSettlementState.Recorded,
                SettledAt       = now,
            };

            var (_, inserted) = await _settlementStore.TryInsertAsync(intent, ct);
            if (inserted)
            {
                _log.LogInformation(
                    "COD settlement intent created deliveryId={DeliveryId} jeeberId={JeeberId}",
                    deliveryId, jeeberId);
            }
        }
        catch (Exception ex)
        {
            // Settlement enqueue is best-effort; OTP verify 200 is canonical.
            _log.LogError(ex,
                "COD settlement intent enqueue failed for deliveryId={DeliveryId}; will be retried at settle time",
                deliveryId);
        }
    }
}
