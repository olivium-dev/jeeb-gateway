using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Availability;
using JeebGateway.Push;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Notifications;

/// <summary>
/// BUILD-NEWREQ-PUSH — the request-created → "finding jeebers" push trigger. When a
/// customer creates a delivery request, every ELIGIBLE jeeber must be nudged so they
/// can open the request and bid.
///
/// <para>FILTERED PER-USER FAN-OUT (P1): the create hot path only ENQUEUES a
/// <see cref="NewRequestNotification"/> (O(1), no I/O) onto
/// <see cref="INewRequestFanoutQueue"/> and returns 201 immediately.
/// <see cref="NewRequestFanoutProcessor"/> drains the queue on a background loop and
/// calls <see cref="FanOutAsync"/> from a fresh DI scope, which resolves the recipient
/// set from the gateway-owned <c>jeeber_availability</c> table and sends ONE per-user
/// push per recipient over the same <c>Send_notification_to_userAsync</c> rail
/// <see cref="OfferPushNotifier"/> already uses. Zero topic sends on the default path.</para>
///
/// <para>The ladder (implemented literally in <c>ResolveRecipientsAsync</c>):
/// <list type="number">
///   <item><description><c>ListOnlineAsync()</c> → <c>source=online</c>.</description></item>
///   <item><description>Optional geo narrowing around the pickup point when
///     <see cref="NewRequestFanoutOptions.RadiusKm"/> is set (ships NULL/off) — rows with
///     NO stored coordinates are never dropped, and a filter that empties the set is
///     logged and IGNORED rather than allowed to kill the auction → <c>source=online+geo</c>.</description></item>
///   <item><description>When nobody is online, <c>ListKnownJeebersAsync(now - KnownJeeberWindow)</c>
///     → <c>source=known</c>. Over-notification must never become under-notification.</description></item>
///   <item><description>Drop blank ids, DROP THE INITIATOR (the P1 fix), de-duplicate,
///     cap at <see cref="NewRequestFanoutOptions.MaxRecipients"/>.</description></item>
///   <item><description>Send per recipient with bounded parallelism; one structured
///     <c>newreq-fanout</c> log line carries the whole decision.</description></item>
/// </list></para>
///
/// <para>WHY <c>jeeber_availability</c> IS A SAFE AUDIENCE SOURCE:
/// <c>AvailabilityController</c> is annotated class-level
/// <c>[RequireCapability(Capabilities.AvailabilityToggle)]</c> and is the only writer into
/// <see cref="IAvailabilityStore"/> on a user path, so every row in the table was created by
/// a caller holding the jeeber capability. The table IS a jeeber roster — a customer-only
/// account can never appear in it, with no per-recipient role lookup needed.</para>
///
/// <para>ROLLBACK: <c>Notifications:NewRequestFanout:Enabled=false</c> restores the legacy
/// <c>jeeb_jeebers</c> topic blast byte-identically, with no redeploy. See
/// <see cref="NewRequestFanoutOptions"/>.</para>
///
/// <para>Reuses the EXISTING, deployed <see cref="ServicePushNotificationClient"/> (the same
/// typed client + base URL :10040 that <see cref="ChatMessagePushNotifier"/> and
/// <see cref="OfferPushNotifier"/> use) — no new push contract is invented, and the route
/// <c>POST api/v1/sent-payload/user/{user_id}</c> is already in daily use.</para>
///
/// <para>DEGRADE-DON'T-FAIL: best-effort. It NEVER throws and never affects the create 201 —
/// every failure (relay blip, timeout) is logged and swallowed. A per-recipient failure is
/// logged at Debug (a recipient with no registered device makes the relay 404, which is
/// routine steady-state noise in a fan-out) and NEVER aborts the batch; the operator-facing
/// signal is the aggregate <c>failed=</c> count on the summary line.</para>
///
/// <para>FCM DATA SHAPE: unchanged from the topic era, byte-identical on the wire so
/// pre-P1 APKs keep deep-linking. The relay at :10040 copies each top-level payload entry
/// (other than <c>title</c>/<c>body</c>, which become the FCM notification block) into the
/// FCM data map, stringifying each value. Routing fields are therefore emitted as FLAT
/// top-level string entries. Both the camel (<c>requestId</c>) and snake (<c>request_id</c>)
/// variants of the request id are carried because the mobile deep-link routes
/// <c>/orders/:id</c> from a <c>delivery_id</c> / <c>order_id</c> / <c>requestId</c>
/// fallback and reads whichever it finds. NO field that could identify the initiator is
/// ever added.</para>
/// </summary>
public interface INewRequestPushNotifier
{
    /// <summary>
    /// Hot path. O(1): builds the job and ENQUEUES it. Never throws, never does I/O, never
    /// touches the DB or the relay — create-201 latency is unaffected by recipient count.
    /// A blank <c>RequestId</c> is a no-op.
    /// </summary>
    Task NotifyNewRequestAsync(NewRequestNotification notification, CancellationToken ct);

    /// <summary>
    /// Background path, invoked by <see cref="NewRequestFanoutProcessor"/> from a fresh DI
    /// scope. Resolves recipients, excludes the initiator, and sends one per-user push each.
    /// Never throws.
    /// </summary>
    Task FanOutAsync(NewRequestNotification notification, CancellationToken ct);
}

/// <inheritdoc />
public sealed class NewRequestPushNotifier : INewRequestPushNotifier
{
    // Bounds the FCM round-trip on the legacy topic-blast path (rollback branch) so a
    // slow/down relay cannot wedge the job. Per-recipient sends use
    // NewRequestFanoutOptions.PerSendTimeout instead. Both now come from PushSendBudget.
    private static readonly TimeSpan PushTimeout = PushSendBudget.PerRecipient;

    // FR: the request description is a preview, not the full order — cap it so a long
    // transcript/compose body does not blow up the FCM notification body.
    private const int BodyPreviewMaxLength = 80;

    private const double EarthRadiusKm = 6371.0;

    private readonly ServicePushNotificationClient _push;
    private readonly JeebGateway.Tiers.ITiersStore _tiers;
    private readonly ILogger<NewRequestPushNotifier> _logger;
    private readonly IAvailabilityStore _availability;
    private readonly INewRequestFanoutQueue _queue;
    private readonly NewRequestFanoutOptions _options;
    private readonly TimeProvider _clock;

    public NewRequestPushNotifier(
        ServicePushNotificationClient push,
        JeebGateway.Tiers.ITiersStore tiers,
        ILogger<NewRequestPushNotifier> logger,
        IAvailabilityStore availability,
        INewRequestFanoutQueue queue,
        IOptions<NewRequestFanoutOptions> options,
        TimeProvider clock)
    {
        _push = push;
        _tiers = tiers;
        _logger = logger;
        _availability = availability;
        _queue = queue;
        _options = options.Value;
        _clock = clock;
    }

    // ── Hot path ──────────────────────────────────────────────────────────────

    public Task NotifyNewRequestAsync(NewRequestNotification notification, CancellationToken ct)
    {
        try
        {
            if (notification is null || string.IsNullOrWhiteSpace(notification.RequestId))
            {
                return Task.CompletedTask;
            }

            if (!_queue.TryEnqueue(notification))
            {
                // The buffer is full — the push is dropped, LOUDLY. The create still 201s;
                // it never blocks and never throws on the hot path.
                _logger.LogWarning(
                    "newreq-fanout queue full; dropping push for {RequestId}", notification.RequestId);
            }
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: the request row was already durable and the 201 is
            // committed. An enqueue fault must never surface to the create path.
            _logger.LogWarning(ex,
                "New-request push enqueue for request {RequestId} failed; create stays 201.",
                notification?.RequestId ?? "(none)");
        }

        return Task.CompletedTask;
    }

    // ── Background path ───────────────────────────────────────────────────────

    public async Task FanOutAsync(NewRequestNotification n, CancellationToken ct)
    {
        var started = _clock.GetUtcNow();
        try
        {
            if (n is null || string.IsNullOrWhiteSpace(n.RequestId))
            {
                return;
            }

            var payload = await BuildPayloadAsync(n, ct);

            if (!_options.Enabled)
            {
                // Config-only rollback: byte-identical to the pre-P1 topic blast.
                await SendTopicBlastAsync(payload, ct);
                return;
            }

            var resolved = await ResolveRecipientsAsync(n, ct);

            if (resolved.Recipients.Count == 0)
            {
                _logger.LogWarning(
                    "newreq-fanout requestId={RequestId} source={Source} candidates={Candidates} recipients=0 "
                    + "initiatorExcluded={InitiatorExcluded} — NO PUSH SENT (no online or known jeeber)",
                    n.RequestId, resolved.Source, resolved.CandidateCount, resolved.InitiatorExcluded);

                if (_options.TopicFallbackWhenEmpty)
                {
                    await SendTopicBlastAsync(payload, ct);
                }
                return;
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_options.TotalBudget);
            var tally = await SendAllAsync(resolved.Recipients, payload, budget.Token);

            _logger.LogInformation(
                "newreq-fanout requestId={RequestId} source={Source} candidates={Candidates} recipients={Recipients} "
                + "initiatorExcluded={InitiatorExcluded} sent={Sent} failed={Failed} "
                + "fcmAcceptedRows={AcceptedRows} fcmRejectedRows={RejectedRows} elapsedMs={ElapsedMs}",
                n.RequestId, resolved.Source, resolved.CandidateCount, resolved.Recipients.Count,
                resolved.InitiatorExcluded, tally.Sent, tally.Failed,
                tally.DeviceRowsAccepted, tally.DeviceRowsRejected,
                (int)(_clock.GetUtcNow() - started).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "New-request fan-out for request {RequestId} failed; create stays 201.",
                n?.RequestId ?? "(none)");
        }
    }

    // ── Recipient resolution ──────────────────────────────────────────────────

    private sealed record ResolvedRecipients(
        IReadOnlyList<string> Recipients,
        string Source,
        int CandidateCount,
        int InitiatorExcluded);

    private async Task<ResolvedRecipients> ResolveRecipientsAsync(
        NewRequestNotification n, CancellationToken ct)
    {
        // 1. online jeebers first.
        var source = "online";
        IReadOnlyList<JeeberAvailability> candidates = await _availability.ListOnlineAsync(ct)
            ?? Array.Empty<JeeberAvailability>();

        // 2. optional geo narrowing around the pickup point. Ships DISABLED (RadiusKm null).
        if (_options.RadiusKm is { } radiusKm && radiusKm > 0
            && n.PickupLat is { } pickupLat && n.PickupLng is { } pickupLng)
        {
            var near = candidates
                .Where(r => !HasCoordinates(r)
                            || HaversineKm(r.Latitude!.Value, r.Longitude!.Value, pickupLat, pickupLng) <= radiusKm)
                .ToArray();

            if (near.Length == 0 && candidates.Count > 0)
            {
                // A mis-set radius must never kill the reverse auction.
                _logger.LogWarning(
                    "newreq-fanout geo-filter-emptied requestId={RequestId} radiusKm={RadiusKm} "
                    + "candidates={Candidates}; keeping the UNFILTERED online set",
                    n.RequestId, radiusKm, candidates.Count);
            }
            else
            {
                candidates = near;
                source = "online+geo";
            }
        }

        // 3. never-starve fallback: the known-jeeber roster (R1).
        if (candidates.Count == 0 && _options.FallbackToKnownJeebers)
        {
            var since = _clock.GetUtcNow() - _options.KnownJeeberWindow;
            candidates = await _availability.ListKnownJeebersAsync(since, ct)
                ?? Array.Empty<JeeberAvailability>();
            source = "known";
        }

        var candidateCount = candidates.Count;

        // 4-6. drop blanks, DROP THE INITIATOR (the P1 fix), de-duplicate.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recipients = new List<string>(candidateCount);
        var initiatorExcluded = 0;

        foreach (var row in candidates)
        {
            var userId = row?.UserId?.Trim();
            if (string.IsNullOrWhiteSpace(userId))
            {
                continue;
            }

            if (SameUser(userId, n.InitiatorUserId))
            {
                initiatorExcluded++;
                continue;
            }

            if (!seen.Add(userId))
            {
                continue;
            }

            recipients.Add(userId);
        }

        // 7. cap.
        var cap = Math.Max(0, _options.MaxRecipients);
        if (recipients.Count > cap)
        {
            _logger.LogWarning(
                "newreq-fanout recipients-truncated requestId={RequestId} resolved={Resolved} cap={Cap}",
                n.RequestId, recipients.Count, cap);
            recipients = recipients.GetRange(0, cap);
        }

        return new ResolvedRecipients(recipients, source, candidateCount, initiatorExcluded);
    }

    /// <summary>
    /// The initiator match. Required because <c>PostgresAvailabilityStore.MapRow</c> emits
    /// <c>Guid.ToString()</c> (lowercase "D" format) while the initiator id comes from the JWT
    /// <c>sub</c> and may differ in case/format. When BOTH sides parse as a <see cref="Guid"/>
    /// the comparison is Guid equality; otherwise it is a trimmed case-insensitive string match.
    /// </summary>
    private static bool SameUser(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var left = a.Trim();
        var right = b.Trim();

        if (Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid))
        {
            return leftGuid == rightGuid;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCoordinates(JeeberAvailability r)
        => r.Latitude is not null && r.Longitude is not null;

    /// <summary>Great-circle distance in kilometres.</summary>
    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                   * Math.Sin(dLng / 2) * Math.Sin(dLng / 2));
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    // ── Sending ───────────────────────────────────────────────────────────────

    private sealed class SendTally
    {
        /// <summary>Recipients whose per-user call completed. NOT a delivery count.</summary>
        public int Sent;

        /// <summary>Recipients whose per-user call threw (404 no rows, 500 all rows dead, timeout).</summary>
        public int Failed;

        /// <summary>Device rows FCM accepted, summed over the recipients that answered.</summary>
        public int DeviceRowsAccepted;

        /// <summary>Device rows FCM rejected, summed over the recipients that answered.</summary>
        public int DeviceRowsRejected;
    }

    private async Task<SendTally> SendAllAsync(
        IReadOnlyList<string> recipients, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var tally = new SendTally();
        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelSends));

        var tasks = new List<Task>(recipients.Count);
        foreach (var userId in recipients)
        {
            tasks.Add(SendOneAsync(userId));
        }

        await Task.WhenAll(tasks);
        return tally;

        async Task SendOneAsync(string userId)
        {
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (Exception)
            {
                // The whole-job budget expired before this recipient got a slot.
                Interlocked.Increment(ref tally.Failed);
                return;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_options.PerSendTimeout);

                var accepted = await _push.Send_notification_to_userAsync(
                    userId,
                    new SentPayloadToUserRequest { Payload = payload },
                    cts.Token);

                Interlocked.Increment(ref tally.Sent);

                // The summary line's sent=/failed= counters are recipient-level and have
                // misled two batches on their own (they counted a push "failed" that a human
                // then watched arrive). Carry the push service's device-row accounting up to
                // the summary so the operator signal is about tokens, not about whether the
                // gateway's own CTS beat the response.
                var rows = PushAcceptance.Parse(accepted);
                if (rows.Accepted is int a)
                {
                    Interlocked.Add(ref tally.DeviceRowsAccepted, a);
                }

                if (rows.Failed is int f)
                {
                    Interlocked.Add(ref tally.DeviceRowsRejected, f);
                }
            }
            catch (Exception ex)
            {
                // A recipient with no registered device makes the relay 404 → ApiException.
                // That is routine steady-state noise in a fan-out, so it is DEBUG, not
                // Warning; the aggregate failed= count on the summary line is the operator
                // signal. A per-recipient failure must NEVER abort the batch.
                Interlocked.Increment(ref tally.Failed);
                _logger.LogDebug(ex,
                    "newreq-fanout per-recipient send failed for user {UserId}; continuing", userId);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// The pre-P1 behaviour, verbatim: ONE push to the <c>jeeb_jeebers</c> FCM topic, which
    /// reaches ALL subscribed jeebers regardless of geography, availability, or who created
    /// the request. Reachable only via the <c>Enabled=false</c> rollback branch or the
    /// <c>TopicFallbackWhenEmpty</c> escape hatch (both default to the safe setting).
    /// </summary>
    private async Task SendTopicBlastAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PushTimeout);

        await _push.Send_notification_to_topicAsync(
            JeebPushTopicMap.JeebersTopic,
            new SentPayloadToTopicRequest { Payload = payload },
            cts.Token);
    }

    // ── Payload ───────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, object?>> BuildPayloadAsync(
        NewRequestNotification n, CancellationToken ct)
    {
        // Resolve the human tier LABEL for the body suffix from the gateway's in-process tier
        // catalog (the same store served at GET /v1/tiers). When the id does not resolve to a
        // catalog row (an opaque UUID, or a code from a divergent taxonomy), the label is null
        // and the suffix is DROPPED — a raw id/UUID is never shown to the jeeber. The raw
        // tierId still travels as its own flat machine field below for client-side filtering,
        // which is unaffected by display resolution.
        var tierLabel = await ResolveTierLabelAsync(n.TierId, ct);

        return new Dictionary<string, object?>
        {
            ["title"] = "New delivery request",
            ["body"] = BuildBody(n.Description, tierLabel),
            ["type"] = "new_request",
            ["category"] = "delivery",
            // F5 (JEBV4-302) AUDIENCE SCOPE HINT — this new-request notification is for
            // JEEBERS ONLY (the reverse-auction "finding jeebers" fan-out). Since P1 it is
            // delivered per-user to a resolved jeeber set rather than blasted to a topic, so
            // the hint is no longer load-bearing for privacy — it is kept because the mobile
            // client reads it as a defence-in-depth receive guard and old APKs already expect
            // it. It is deliberately NOT the initiator's id: nothing that could identify the
            // creator ever goes on this wire.
            ["audience"] = "jeebers",
            ["audience_role"] = JeebGateway.Users.Roles.Jeeber,
            // High-priority delivery hint. A new-request "finding jeebers" push is
            // time-sensitive (the reverse auction is open only briefly), so it must wake the
            // device rather than be batched. NOTE: this is a FLAT hint the relay copies into
            // the FCM data map; the actual FCM `android.priority` / apns-priority elevation is
            // owned by the push-notification-service (:10040), which must read this hint to
            // honour it (issue-24 / SVC-6).
            ["priority"] = "high",
            // Both camel + snake variants — the mobile deep-link reads either
            // (routes /orders/:id from delivery_id/order_id/requestId fallback).
            ["requestId"] = n.RequestId,
            ["request_id"] = n.RequestId,
            // tierId is carried flat too so the jeeber client can pre-filter/label without a
            // second round-trip. Null when the request has no tier. This is the RAW id
            // (machine field) — distinct from the resolved display label baked into the body.
            ["tierId"] = n.TierId,
        };
    }

    /// <summary>
    /// Best-effort resolve of a tier id to its human display name via the gateway's
    /// in-process tier catalog. feat/tier-unify-names: the id is first canonicalized
    /// through <see cref="JeebGateway.Tiers.LegacyTierCodes"/>, so legacy codes
    /// (flash/express/standard/on_the_way/eco) resolve to their aliased catalog row's
    /// display name instead of silently dropping the suffix. Returns null when the id
    /// is blank or does not match a catalog row — the caller then DROPS the body
    /// suffix rather than render a raw id/UUID. Never throws: a catalog hiccup
    /// degrades to "no suffix", never to a failed push (the whole notify path is
    /// degrade-don't-fail).
    /// </summary>
    private async Task<string?> ResolveTierLabelAsync(string? tierId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierId))
        {
            return null;
        }

        try
        {
            var tier = await _tiers.GetAsync(
                JeebGateway.Tiers.LegacyTierCodes.Canonicalize(tierId), ct);
            return string.IsNullOrWhiteSpace(tier?.Name) ? null : tier!.Name.Trim();
        }
        catch (Exception ex)
        {
            // The catalog lookup must never break the push. Fall back to "no suffix".
            _logger.LogDebug(ex,
                "Tier-label resolve failed for tier {TierId}; dropping the body suffix.", tierId);
            return null;
        }
    }

    /// <summary>
    /// Body = the description preview (trimmed to <see cref="BodyPreviewMaxLength"/>
    /// chars) plus a " • {tier}" suffix ONLY when a human tier LABEL was resolved
    /// (<see cref="ResolveTierLabelAsync"/>). When no label is available the suffix
    /// is dropped — a raw tier id/UUID is never surfaced in the notification body.
    /// </summary>
    private static string BuildBody(string? description, string? tierLabel)
    {
        var preview = Trim(description);
        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = "A customer needs a delivery. Tap to view and bid.";
        }

        return string.IsNullOrWhiteSpace(tierLabel)
            ? preview
            : $"{preview} • {tierLabel.Trim()}";
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= BodyPreviewMaxLength
            ? trimmed
            : trimmed.Substring(0, BodyPreviewMaxLength);
    }
}
