using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Notifications;
using JeebGateway.StateService.Idempotency;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// JEBV4-341 (batch b02, step 4) — the INBOUND service callback surface.
///
/// <para><b>Why it exists.</b> The push architecture locked by the owner on 2026-07-26 has three
/// rules: (1) no long polling; (2) the gateway never talks to FCM itself — every push leaves via the
/// shared push microservice; (3) <b>no microservice calls the push microservice directly</b> — a
/// microservice that wants a user notified calls BACK into the gateway, and the gateway decides
/// taxonomy, copy, locale, deep link and transport. Before this controller there was no legal door
/// for rule 3: the only <c>/internal</c> route in the whole gateway was
/// <c>GET internal/health</c> (<see cref="InternalController"/>), and the two endpoints that look
/// like this door are not one —
/// <c>POST /v1/notifications/dispatch</c> is admin-capability-gated, and
/// <c>POST api/PushNotification/user</c> derives the recipient from the CALLER's own token, so a
/// microservice cannot address another user with it.</para>
///
/// <para><b>Why <c>/svc-callbacks/</c> and not <c>/internal/</c>.</b> Not an auth decision. The
/// gateway's <see cref="Security.ApiKeyAuthenticationMiddleware"/> gates on
/// <c>path.StartsWithSegments("/internal")</c> and that check exempts NEITHER
/// <c>[AllowAnonymous]</c> nor <see cref="PublicEndpointAttribute"/> — it runs before routing and
/// sees no endpoint metadata at all. So anything mounted under <c>/internal/</c> becomes hostage to
/// a future flip of <c>Security:ApiKey:Enabled</c>, which would also 401 the public liveness probe
/// at <c>GET /internal/health</c> and fail the deploy health gate. Staying off that prefix costs
/// nothing and prevents that accident.</para>
///
/// <para><b>Authentication: none, by owner ruling of 2026-07-26</b> — <i>"between microservices
/// follow raham, no auth for now."</i> There is deliberately no <c>ServiceAuth</c>, no
/// <c>X-Api-Key</c> requirement and no toggle hedge on this route. The accepted consequence, stated
/// once: the endpoint takes <c>recipientUserId</c> from the caller, so any peer that can reach the
/// gateway on the service network can notify any user. Reachability is the only control. This is an
/// accepted risk owned by the owner, recorded here so a future reader does not mistake it for a
/// satisfied rule.</para>
///
/// <para><b>Statelessness.</b> The gateway holds no row. Idempotency lives in
/// <see cref="IIdempotencyStore"/>, whose production implementation persists to jeeb-state-service
/// (ADR-0001/0005); the in-process implementation is the local/CI fallback only.</para>
/// </summary>
[ApiController]
[Route("svc-callbacks")]
[Produces("application/json", "application/problem+json")]
public sealed class ServiceCallbacksController : ControllerBase
{
    /// <summary>
    /// Namespace for the idempotency rows this controller owns, so a caller-chosen key can never
    /// collide with a client <c>Idempotency-Key</c> handled by
    /// <see cref="IdempotencyMiddleware"/> or with any other durable KV user.
    /// </summary>
    private const string IdempotencyKeyPrefix = "svc-callback-notify:";

    /// <summary>
    /// Dedup window. Callbacks retry on a timer measured in seconds-to-minutes; a day of memory is
    /// far past any sane retry schedule and still bounded so the KV does not grow without limit.
    /// </summary>
    private const int IdempotencyTtlSeconds = 24 * 60 * 60;

    /// <summary>
    /// Bounds the push round-trip so a slow push microservice cannot hold the 202 open.
    ///
    /// <para>Was a seat-local 5s. That did clear the measured healthy call (2.53-3.97s across
    /// 10 consecutive sends, 2026-07-28) — but only by 1.26x, and the per-call cost is linear
    /// in the recipient's device-row count, which only ever grows because the push service
    /// never deletes a row whose send failed. 1.26x of head-room on a number that drifts
    /// upward is the same under-sizing that left every other seat on 2s; this seat was one
    /// row-growth cycle from repeating it. Sourced from
    /// <see cref="JeebGateway.Notifications.PushSendBudget.PerRecipient"/> now, so there is no
    /// seat-local copy left to drift.</para>
    ///
    /// <para><b>Inline exposure, accepted deliberately.</b> Unlike the offer seats this one is
    /// still awaited in front of its response, so the worst case grows 5s -> 10s. That is
    /// bounded and single-recipient (this seat pushes to exactly one user, never a fan-out),
    /// and the caller is a microservice with its own retry schedule — not the mobile client
    /// whose receive timeout made the same arithmetic unsafe on the offer-accept path.</para>
    /// </summary>
    private static readonly TimeSpan PushTimeout = JeebGateway.Notifications.PushSendBudget.PerRecipient;

    // NOTE — there is no longer a "not yet routable types" deny-list here. It held exactly one
    // entry, jeeb.offer_rejected, for execution-plan correction C6: the catalog declared nine
    // jeeb.* types while the notification centre served eight, and that ninth answered 405 where
    // every served type answers 422. b02 step 6b resolved that at the source (owner ruling D3 =
    // retire): the type is gone from JeebNotificationCatalog, so the HasTemplate check above now
    // rejects it as an unknown type and a second overlapping gate would be dead code. Every key
    // the catalog still declares has a live centre route — verified by probing all eight.
    //
    // If a future catalog key is added whose centre route does not exist, do NOT reintroduce a
    // deny-list: add the route, or do not add the key.

    /// <summary>Payload keys the gateway owns; a caller's <c>data</c> may not overwrite them.</summary>
    private static readonly HashSet<string> ReservedPayloadKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "title", "body", "silent", "type", "category", "deepLink", "notificationId",
        };

    // D1 single-producer: the in-gateway offer seats (OfferPushNotifier) already produce these two;
    // offer-service's Oban callback for them is the redundant producer and is skipped below.
    private static readonly HashSet<string> InGatewayPushProducedTypes =
        new(StringComparer.Ordinal)
        {
            OfferReceivedNotificationRecord.TemplateKey,
            OfferAcceptedNotificationRecord.TemplateKey,
        };

    private readonly ServicePushNotificationClient _push;
    private readonly IIdempotencyStore _idempotency;
    private readonly INotificationRecordWriter _records;
    private readonly IGenericEventDispatcher _events;
    private readonly ILogger<ServiceCallbacksController> _log;

    public ServiceCallbacksController(
        ServicePushNotificationClient push,
        IIdempotencyStore idempotency,
        INotificationRecordWriter records,
        IGenericEventDispatcher events,
        ILogger<ServiceCallbacksController> log)
    {
        _push = push;
        _idempotency = idempotency;
        _records = records;
        _events = events;
        _log = log;
    }

    /// <summary>
    /// <c>POST /svc-callbacks/notify</c> — the single inbound door for a microservice-originated
    /// user notification. The gateway validates the type against
    /// <see cref="JeebNotificationCatalog"/>, renders the localized copy, resolves the deep link,
    /// and dispatches through the shared push microservice via
    /// <see cref="ServicePushNotificationClient"/> (rule 2 — the gateway never touches FCM).
    /// </summary>
    /// <response code="202">Accepted. <c>wasDeduplicated</c> is true when this key was already seen.</response>
    /// <response code="400">Unknown/unroutable notificationType, blank recipientUserId, or missing idempotencyKey.</response>
    [HttpPost("notify")]
    // ADR-004 opt-out: unauthenticated by design (owner ruling 2026-07-26, no inter-microservice auth).
    [AllowAnonymous]
    // ADR-005 §A marker. [AllowAnonymous] alone does NOT satisfy the CapabilityCoverageGuard —
    // this attribute is what declares the action intentionally uncovered rather than accidentally so.
    [PublicEndpoint(
        "Inbound microservice notification callback (JEBV4-341). Intentionally unauthenticated per "
        + "owner ruling of 2026-07-26: no authorization between microservices. Reachability on the "
        + "service network is the only control; accepted risk owned by the owner.")]
    [ProducesResponseType(typeof(ServiceCallbackNotifyResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Notify(
        [FromBody] ServiceCallbackNotifyRequest? body,
        CancellationToken ct)
    {
        if (body is null)
        {
            return Invalid("A JSON request body is required.");
        }

        var notificationType = body.NotificationType?.Trim();
        if (string.IsNullOrEmpty(notificationType))
        {
            return Invalid("notificationType is required.");
        }

        if (!JeebNotificationCatalog.HasTemplate(notificationType))
        {
            // The catalog is the taxonomy boundary: an unknown type would otherwise render the
            // product-neutral fallback copy ("You have a new notification for X") and ship it to a
            // real device. Reject instead of guessing.
            return Invalid(
                $"Unknown notificationType '{notificationType}'. Known types: "
                + string.Join(", ", JeebNotificationCatalog.Keys.OrderBy(k => k, StringComparer.Ordinal))
                + ".");
        }

        var recipientUserId = body.RecipientUserId?.Trim();
        if (string.IsNullOrEmpty(recipientUserId))
        {
            return Invalid("recipientUserId is required.");
        }

        // REQUIRED, not optional. Callbacks retry, and a retry without a stable key is
        // indistinguishable from a second event — which is exactly how a duplicated shade entry
        // reaches production while every review stays green.
        var callerKey = body.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(callerKey))
        {
            return Invalid("idempotencyKey is required — service callbacks retry.");
        }

        // Deliberately AFTER validation (a malformed callback must still 400) and BEFORE the
        // reservation, so a no-op burns no idempotency row and a retry simply re-skips.
        if (InGatewayPushProducedTypes.Contains(notificationType))
        {
            _log.LogInformation(
                "event={event} type={type} recipientId={recipientId} idempotencyKey={key}",
                "svc_callback.skipped_in_gateway_producer", notificationType, recipientUserId, callerKey);

            return Accepted(new ServiceCallbackNotifyResponse
            {
                EntryId = Guid.NewGuid().ToString("D"),
                WasDeduplicated = false,
                Status = DispatchStatus.Skipped,
            });
        }

        var entryId = Guid.NewGuid().ToString("D");
        var storageKey = IdempotencyKeyPrefix + callerKey;

        // Reserve BEFORE sending, so two concurrent retries of the same callback cannot both
        // dispatch. The store's contract is insert-once (INSERT … ON CONFLICT DO NOTHING): exactly
        // one caller gets Inserted=true.
        //
        // Residual, deliberately accepted here: the key is consumed even when the subsequent push
        // dispatch fails, so a later retry of that callback dedupes instead of re-delivering. That
        // is the trade the target architecture chose — a duplicated shade entry is a user-visible
        // defect that no test catches, a single missed best-effort push is not. Releasing a
        // reservation safely needs a compare-and-set the store does not expose today.
        IdempotencyOutcome reservation;
        try
        {
            reservation = await _idempotency.PutOrGetAsync(
                storageKey,
                StatusCodes.Status202Accepted,
                JsonSerializer.Serialize(new ReservedResponse(entryId, DispatchStatus.Queued)),
                IdempotencyTtlSeconds,
                ct);
        }
        catch (Exception ex)
        {
            // The dedup store is the only thing standing between a retrying callback and a
            // duplicated notification. If it cannot answer we must NOT fall through and push
            // undeduped — fail closed and let the caller retry.
            _log.LogError(ex,
                "event={event} type={type} recipientId={recipientId} idempotencyKey={key}",
                "svc_callback.idempotency_unavailable", notificationType, recipientUserId, callerKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Idempotency store unavailable; retry.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/svc-callback-idempotency-unavailable",
            });
        }

        if (!reservation.Inserted)
        {
            var original = ReadReserved(reservation.ResponseBodyJson);
            _log.LogInformation(
                "event={event} type={type} recipientId={recipientId} idempotencyKey={key} entryId={entryId}",
                "svc_callback.deduplicated", notificationType, recipientUserId, callerKey, original.EntryId);

            // Replay: the ORIGINAL entryId and status, wasDeduplicated=true, and NO second push.
            return Accepted(new ServiceCallbackNotifyResponse
            {
                EntryId = original.EntryId,
                WasDeduplicated = true,
                Status = original.Status,
            });
        }

        var template = JeebNotificationCatalog.Render(notificationType, body.Locale, body.Parameters);
        var entityId = ResolveEntityId(body.Data);
        var payload = BuildPayload(notificationType, template, body, entityId);

        // b02 step 6a — the inbox row, BEFORE the push. (The offer seats no longer push after
        // their write at all: that POST IS the push.) Row-first is the right order: the shade entry
        // is the user's notice that something happened, and the row is what they open afterwards, so
        // if one of the two is going to be missing it must not be the row that the tap lands on.
        //
        // Never fatal to the 202: the callback's contract is best-effort dispatch, and the writer
        // itself never throws (single attempt + read-back classification). A silent type returns
        // SkippedSilent and writes nothing — that gate lives in NotificationRecordWriter, not here,
        // so this call site cannot accidentally bypass it.
        await TryWriteCentreRowAsync(
            notificationType, recipientUserId, template, body.Data, entityId, entryId, ct);

        // D1: this controller and the in-gateway notifiers are two producers for one event.
        // The shared correlation id is what collapses them at notification-service's upsert.
        var handover = await TryHandOverGenericEventAsync(
            notificationType, recipientUserId, template, payload, entityId ?? entryId, ct);
        if (handover != GenericEventDispatchClassification.SkippedDirectDispatchArmed)
        {
            return Accepted(new ServiceCallbackNotifyResponse
            {
                EntryId = entryId,
                WasDeduplicated = false,
                Status = handover == GenericEventDispatchClassification.Unproven
                    ? DispatchStatus.Failed
                    : DispatchStatus.Queued,
            });
        }

        var status = DispatchStatus.Queued;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            // Rule 2 — the push leaves via the shared push microservice, never via FCM from here.
            await _push.Send_notification_to_userAsync(
                recipientUserId,
                new SentPayloadToUserRequest { Payload = payload },
                cts.Token);

            _log.LogInformation(
                "event={event} type={type} recipientId={recipientId} idempotencyKey={key} "
                + "entryId={entryId} silent={silent}",
                "svc_callback.dispatched", notificationType, recipientUserId, callerKey, entryId, body.Silent);
        }
        catch (Exception ex)
        {
            status = DispatchStatus.Failed;
            _log.LogError(ex,
                "event={event} type={type} recipientId={recipientId} idempotencyKey={key} entryId={entryId}",
                "svc_callback.dispatch_failed", notificationType, recipientUserId, callerKey, entryId);
        }

        return Accepted(new ServiceCallbackNotifyResponse
        {
            EntryId = entryId,
            WasDeduplicated = false,
            Status = status,
        });
    }

    /// <summary>
    /// b02 step 6a — write the durable notification-centre row for a type that has a writer.
    ///
    /// <para>Push-only types (the two offer seats, which mint their own rows from authoritative
    /// store data) are skipped by <see cref="ServiceCallbackRecordFactory.CanWrite"/> rather than
    /// double-written; the centre does not deduplicate on <c>notification_id</c>, so two producers
    /// on one taxonomy would mean two inbox rows for one event.</para>
    /// </summary>
    private async Task TryWriteCentreRowAsync(
        string notificationType,
        string recipientUserId,
        NotificationTemplate template,
        IReadOnlyDictionary<string, string>? data,
        string? entityId,
        string entryId,
        CancellationToken ct)
    {
        if (!ServiceCallbackRecordFactory.CanWrite(notificationType))
        {
            return;
        }

        try
        {
            // The correlation handle joins this row to its push. When the caller sent no entity id
            // we fall back to the gateway-minted entryId: NotificationCorrelationId.Create rejects a
            // blank component, and an entryId-derived handle is still unique and still greppable —
            // it just is not stable across a retry that omits the id, which is acceptable because
            // the idempotency reservation already stopped the retry before reaching here.
            var ncid = NotificationCorrelationId.Create(
                notificationType, recipientUserId, entityId ?? entryId);

            var write = ServiceCallbackRecordFactory.WriteAsync(
                _records, notificationType, recipientUserId, template, data, ncid, ct);

            if (write is null)
            {
                return;
            }

            var outcome = await write;

            _log.LogInformation(
                "event={event} type={type} recipientId={recipientId} entryId={entryId} "
                + "ncid={ncid} classification={classification} upstreamStatus={upstreamStatus}",
                "svc_callback.centre_write", notificationType, recipientUserId, entryId,
                ncid, outcome.Classification, outcome.UpstreamStatus);
        }
        catch (Exception ex)
        {
            // Degrade, do not fail: the caller's event already happened upstream and the push is
            // still worth attempting. A lost row is loud in the log above and in
            // notif.durable_write.* telemetry; a 5xx here would make the caller retry the whole
            // callback and risk a duplicate shade entry.
            _log.LogError(ex,
                "event={event} type={type} recipientId={recipientId} entryId={entryId}",
                "svc_callback.centre_write_failed", notificationType, recipientUserId, entryId);
        }
    }

    private BadRequestObjectResult Invalid(string title) => BadRequest(new ProblemDetails
    {
        Title = title,
        Status = StatusCodes.Status400BadRequest,
        Type = "https://jeeb.dev/errors/svc-callback-invalid",
    });

    /// <summary>
    /// The FCM data block the push microservice will emit. It copies every top-level payload entry
    /// other than <c>title</c>/<c>body</c> (which become the FCM notification block) into the data
    /// map, stringifying each value — so routing fields are written FLAT and the mobile client needs
    /// no nested-JSON hoist. Mirrors <see cref="OfferPushNotifier"/> exactly; no new push contract
    /// is invented here.
    /// </summary>
    private static Dictionary<string, object?> BuildPayload(
        string notificationType,
        NotificationTemplate template,
        ServiceCallbackNotifyRequest body,
        string? entityId)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Caller passthrough first, so a reserved key can never be shadowed by it.
        if (body.Data is not null)
        {
            foreach (var kv in body.Data)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || ReservedPayloadKeys.Contains(kv.Key))
                {
                    continue;
                }

                payload[kv.Key] = kv.Value;
            }
        }

        payload["title"] = template.Title;
        payload["body"] = template.Body;
        payload["type"] = notificationType;
        payload["category"] = "notification";
        payload["deepLink"] = NotificationDeepLinkResolver.Resolve(notificationType, entityId);

        // `silent` is forwarded as the transport-level request field the push microservice will read
        // once the data-only path lands (target doc §5.4). TODAY every sent-payload endpoint attaches
        // an FCM notification block unconditionally, so this flag is INERT and the push still reaches
        // the shade. It is carried now so the callback contract does not have to change later.
        payload["silent"] = body.Silent;

        if (entityId is not null)
        {
            var ncid = NotificationCorrelationId.Create(notificationType, RecipientFor(body), entityId);
            payload["notificationId"] = ncid;
        }

        return payload;
    }

    private static string RecipientFor(ServiceCallbackNotifyRequest body)
        => body.RecipientUserId!.Trim();

    /// <summary>
    /// The primary entity id used to fill the <c>{id}</c> token in a deep-link template. Callers put
    /// it in <c>data</c>; we accept the camel and snake spellings the mobile client already reads.
    /// Absent ⇒ the resolver returns the inbox root rather than a malformed link.
    /// </summary>
    /// <summary>
    /// Hands the event to notification-service under the SAME correlation id the in-gateway
    /// notifiers mint, so one logical event yields one stored command and one dispatch.
    /// </summary>
    private async Task<GenericEventDispatchClassification> TryHandOverGenericEventAsync(
        string notificationType,
        string recipientUserId,
        NotificationTemplate template,
        IReadOnlyDictionary<string, object?> payload,
        string entityId,
        CancellationToken ct)
    {
        try
        {
            var routing = payload.ToDictionary(
                kv => kv.Key,
                kv => kv.Value?.ToString() ?? string.Empty,
                StringComparer.Ordinal);

            var outcome = await _events.DispatchAsync(
                notificationType,
                recipientUserId,
                entityId,
                template.Title,
                template.Body,
                routing,
                PushSilencePolicy.CategoryForTemplateKey(notificationType)
                    ?? PushSilencePolicy.CategoryDelivery,
                ct);

            return outcome.Classification;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "event={event} type={type} recipientId={recipientId}",
                "svc_callback.generic_event_failed", notificationType, recipientUserId);
            return GenericEventDispatchClassification.Unproven;
        }
    }

    private static string? ResolveEntityId(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     "entityId", "entity_id",
                     "offerId", "offer_id",
                     "deliveryId", "delivery_id",
                     "requestId", "request_id",
                     "settlementId", "settlement_id",
                     "disputeId", "dispute_id",
                     "id",
                 })
        {
            if (data.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static ReservedResponse ReadReserved(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReservedResponse(string.Empty, DispatchStatus.Queued);
        }

        try
        {
            return JsonSerializer.Deserialize<ReservedResponse>(json)
                   ?? new ReservedResponse(string.Empty, DispatchStatus.Queued);
        }
        catch (JsonException)
        {
            return new ReservedResponse(string.Empty, DispatchStatus.Queued);
        }
    }

    private static class DispatchStatus
    {
        internal const string Queued = "Queued";
        internal const string Failed = "Failed";

        /// <summary>The gateway already produces this event itself; the callback is a no-op.</summary>
        internal const string Skipped = "Skipped";
    }

    /// <summary>What the idempotency row stores, so a replay can answer with the ORIGINAL identity.</summary>
    private sealed record ReservedResponse(string EntryId, string Status);
}

/// <summary>
/// Request body for <c>POST /svc-callbacks/notify</c> (target doc §5.1).
/// </summary>
public sealed class ServiceCallbackNotifyRequest
{
    /// <summary>Must be a key owned by <see cref="JeebNotificationCatalog"/>.</summary>
    public string? NotificationType { get; init; }

    /// <summary>The user to notify. Supplied BY THE CALLER — see the controller's auth note.</summary>
    public string? RecipientUserId { get; init; }

    /// <summary>Optional BCP-47 tag; unsupported/absent falls back to the catalog default.</summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Requests the data-only (no shade entry) transport. Currently inert — see the note in
    /// <c>BuildPayload</c>.
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>REQUIRED. Caller-stable across retries of the SAME callback.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Template substitution only; never forwarded verbatim as copy.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>Opaque passthrough into the FCM data block.</summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}

/// <summary>202 Accepted body for <c>POST /svc-callbacks/notify</c>.</summary>
public sealed class ServiceCallbackNotifyResponse
{
    /// <summary>Gateway-minted handle for this callback. Stable across replays of the same key.</summary>
    public required string EntryId { get; init; }

    /// <summary>True when this key was already seen and NO second push was sent.</summary>
    public required bool WasDeduplicated { get; init; }

    /// <summary><c>Queued</c> when dispatched, <c>Failed</c> when dispatch threw, <c>Skipped</c>
    /// when the gateway itself already produces this event type.</summary>
    public required string Status { get; init; }
}
