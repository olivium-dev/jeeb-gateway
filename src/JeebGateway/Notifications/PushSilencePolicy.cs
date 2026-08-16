using System;
using System.Collections.Generic;

namespace JeebGateway.Notifications;

/// <summary>
/// How ONE notification is delivered. Exactly one of these per event — never both
/// (see <see cref="PushSilencePolicy"/> for why "never both" is the whole point).
/// </summary>
public enum PushDeliveryMode
{
    /// <summary>
    /// Edge-triggered cache-invalidation signal addressed to the APP PROCESS.
    /// Data-only FCM (no shade entry) and <b>ZERO</b> notification-centre rows.
    /// </summary>
    SilentRefresh,

    /// <summary>
    /// Level-held state addressed to a HUMAN. Shade entry plus <b>exactly one</b>
    /// notification-centre row. Its <c>data</c> block MAY additionally carry a
    /// refresh category so the UI re-pulls — that is still ONE push, not two.
    /// </summary>
    ShadeAndStored,
}

/// <summary>
/// b02 step 3 — the gateway's silent-vs-stored policy. ONE place decides, for a given
/// notification type, whether the push is addressed to a human (stored) or to the app
/// process (silent). <see cref="NotificationRecordWriter"/> consults it and refuses the
/// notification-centre write for a silent decision.
///
/// <para><b>WHY A SILENT PUSH MUST NOT BE STORED — do not "fix" this by adding a row.</b>
/// A silent push is <b>EDGE-triggered</b>: it is a cache-invalidation signal whose entire
/// meaning is "the data changed just now, fetch once". A notification-centre row is
/// <b>LEVEL-held</b> state: it survives restart, it is scrolled days later, it is
/// replayable. Storing an edge as level is the same category error as a publisher with no
/// subscriber — it looks correct in code review and produces junk at runtime. Concretely:
/// the inbox fills with machine-addressed chatter the user cannot act on, and a DLQ replay
/// of a stale refresh signal causes a spurious fetch, so the row is not merely useless, it
/// is actively harmful.</para>
///
/// <para>"Skip the write" means <b>NO ROW</b>. Not a soft-deleted row, not a row behind a
/// hidden flag, not a row pre-marked <c>is_read=true</c>. Any of those still costs a POST,
/// still occupies the inbox projection, and is still replayable.</para>
///
/// <para><b>⚠️ SCOPE CORRECTION, 2026-08-11 (D4 contradiction ruling).</b> The paragraph above
/// governs the <b>notification-centre write path</b> (<see cref="NotificationRecordWriter"/>),
/// which is the only path this policy can refuse. It does <b>NOT</b> govern the generic events
/// route (<c>POST /notifications/events</c>, <see cref="GenericEventDispatcher"/>): in the live
/// architecture (gateway direct dispatch OFF, durable write ON) the Mongo row that route
/// creates IS the at-least-once dispatch vehicle AND the notification_id/fingerprint dedupe
/// that currently suppresses the known second-producer duplicate pushes. Skipping it there
/// would break push delivery and re-open duplicates — so for a silent type on that route the
/// row MAY exist. It is instead made harmless at the far end: the record carries
/// <c>data.silent="true"</c> (stamped by <see cref="GenericEventDispatcher.BuildRecord"/> from
/// <see cref="IsSilent"/>, still the single silence authority), notification-service excludes
/// such rows from every receiver-facing read and unread count, and a generic TTL index reaps
/// them. "Silent = invisible and mortal" on that route; "silent = no row" everywhere this
/// policy is actually consulted. Do not reconcile these by deleting either half.</para>
///
/// <para><b>THE COROLLARY (the easy mistake).</b> A change that is BOTH worth telling the
/// user about AND requires a UI refresh is <b>ONE</b> non-silent stored notification whose
/// <c>data</c> block also carries the refresh category. It is <b>NOT</b> two pushes. Two is
/// how you get a duplicated shade for one logical event. This is enforced structurally:
/// <see cref="ModeForTemplateKey"/> is a single-valued total function, so a notification
/// type resolves to exactly one <see cref="PushDeliveryMode"/> and there is no code path
/// that can emit a silent copy alongside a stored one.</para>
///
/// <para><b>STATELESS.</b> Everything here is a pure static lookup over compile-time
/// constants. The gateway holds no notification state; the notification centre
/// owns the stored rows, which is why the only decision made here is
/// whether to call it at all.</para>
///
/// <para><b>⚠️ REACHABILITY — READ THIS BEFORE BELIEVING THE TESTS.</b> The silent branch
/// is <b>unreachable from any live caller</b>, and that is a fact about the codebase, not a
/// defect in this policy. Since the 2026-07-27 reversal (below) <b>no catalog template key
/// is silent at all</b>: every key in <see cref="CategoryByTemplateKey"/> resolves to
/// <see cref="PushDeliveryMode.ShadeAndStored"/>, and the sole remaining silent category,
/// <see cref="CategoryNewRequest"/>, has no template key and no centre writer — its push
/// (<c>NewRequestPushNotifier</c>) never touches the centre. So the silent gate in
/// <see cref="NotificationRecordWriter"/> suppresses <b>nothing</b> in production today; it
/// is a guard pre-placed at the sole choke point, waiting for the first silent type that
/// acquires a writer. Do not describe a green test suite here as proof that a silent push
/// was suppressed in production — nothing has yet asked for one. Equally, do not delete the
/// gate because it is currently dormant: dormant is the intended state of a guard.</para>
///
/// <para><b>⚠️ TWO DEFINITIONS OF "SILENT" ARE IN FLIGHT, and nothing reconciles them.</b>
/// This policy decides silence from the notification <b>type</b> (a static lookup). The
/// push service decides it from a per-send wire flag (<c>payload.get("silent")</c>, b02
/// step 2). They can disagree and no code forces agreement. <b>UPDATED 2026-08-11: the stamp
/// is now wired</b> — <see cref="GenericEventDispatcher.BuildRecord"/> sets
/// <c>data["silent"]="true"</c> sourced from <see cref="ModeForCategory"/>, i.e. from this
/// policy and nowhere else, which is the contract the older wording demanded of whoever wired
/// it. The desync directions it warned about remain the thing to watch: a silent push that
/// still surfaces a row (see the scope correction above — such rows are hidden and TTL-reaped
/// downstream, not absent), or a shade buzz with no row. Any future stamp must likewise
/// source silence from <see cref="IsSilent"/> / <see cref="ModeForCategory"/>.</para>
///
/// <para><b>⚠️ THE FLAT WIRE <c>category</c> FIELD IS NOT A REFRESH CATEGORY.</b> Today's
/// payloads stamp <c>["category"] = "delivery"</c> on new-offer
/// (<c>OfferPushNotifier.cs</c>), new-request (<c>NewRequestPushNotifier.cs</c>) and
/// request-expiry (<c>DispatchingRequestExpiryNotifier.cs</c>) alike — it is a coarse
/// legacy product-area label, not the D4 taxonomy below. It happens to be HARMLESS to feed
/// <c>"delivery"</c> to <see cref="ModeForCategory"/> since the 2026-07-27 reversal made
/// that category stored, and that coincidence is exactly the trap: before the reversal the
/// same line silenced the offer notifications, and a future silent category reachable from
/// this field would silence them again with no test to catch it. The field is a product
/// label that collides with taxonomy names by accident, so resolve the mode from the
/// notification TYPE (template key), never from that field.</para>
///
/// <para><b>⚠️ OWNER RULING REVERSAL, 2026-07-27 — <c>delivery</c> IS A READABLE INBOX
/// ROW.</b> The 2026-07-26 D4 line classified <c>delivery</c> as silent-only. That is
/// <b>SUPERSEDED</b>. Delivery is now <b>shade + stored</b> alongside kyc / settlement /
/// dispute / rating / chat, and <c>newRequest</c> is the ONLY silent-only category left.
/// This is what unblocked <c>jeeb.delivery_status_updated</c>'s centre writer (b02 step 6a):
/// the writer and the four already-merged read paths that assume the row exists
/// (<c>JeebNotificationsInboxController.PayloadRef</c>,
/// <see cref="NotificationDeepLinkResolver"/>, <see cref="JeebNotificationCatalog"/>,
/// <see cref="JeebNotificationCatalogSeeder"/>) are now consistent with this policy instead
/// of contradicting it. If you are reading an older comment, a doc row, or a commit message
/// that says "delivery = silent, no row", it predates this ruling.</para>
/// </summary>
public static class PushSilencePolicy
{
    // ── D4 refresh categories, verbatim from the owner ruling (2026-07-26) ───────────
    // Spelled as the mobile `NotificationCategory` enum names them
    // (jeeb-mobile lib/core/notifications/domain/notification_message.dart).

    /// <summary>
    /// Silent-only per D4 — the jeeber new-request feed refresh signal.
    ///
    /// <para><b>DORMANT, and deliberately so.</b> No catalog template key maps to this
    /// category: the gateway's new-request push (<c>NewRequestPushNotifier</c>) holds no
    /// <see cref="INotificationRecordWriter"/> and there is no <c>jeeb.new_request</c>
    /// catalog template, so there is no centre write for this policy to suppress. The
    /// category is declared because D4 names it, not because it is wired. Since the
    /// 2026-07-27 reversal moved <see cref="CategoryDelivery"/> to the stored side, this is
    /// the ONLY silent-only category left — see the class remarks.</para>
    /// </summary>
    public const string CategoryNewRequest = "newRequest";

    /// <summary>
    /// Shade + stored. <b>REVERSED 2026-07-27</b> — D4 (2026-07-26) had this silent-only;
    /// the owner ruled that a delivery-status change IS a readable inbox row (shade AND
    /// stored), which is what unblocks the <c>jeeb.delivery_status_updated</c> centre
    /// writer. The refresh still happens: per the corollary above, that is ONE stored push
    /// whose <c>data</c> block carries this category, not a second silent push.
    /// </summary>
    public const string CategoryDelivery = "delivery";

    /// <summary>Shade + stored per D4.</summary>
    public const string CategoryKyc = "kyc";

    /// <summary>Shade + stored per D4.</summary>
    public const string CategorySettlement = "settlement";

    /// <summary>Shade + stored per D4.</summary>
    public const string CategoryDispute = "dispute";

    /// <summary>Shade + stored per D4.</summary>
    public const string CategoryRating = "rating";

    /// <summary>Shade + stored per D4.</summary>
    public const string CategoryChat = "chat";

    // ── Categories D4 did not name, classified here to keep the map TOTAL ────────────
    // These are offer-lifecycle events the gateway emits or receives. They are
    // human-addressed ("a jeeber bid on your request", "your offer was accepted",
    // "your offer wasn't selected", "your request found no coverage"), and two of them
    // already have live notification-centre writers. Classifying them ShadeAndStored is
    // therefore BEHAVIOUR-PRESERVING, and it is recorded as such — it is NOT an owner
    // ruling. Do not read the D4 rows above and these rows as equally authoritative.

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryNewOffer = "newOffer";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryOfferAccepted = "offerAccepted";

    /// <summary>Generic offer lifecycle update received from offer-service; ShadeAndStored.</summary>
    public const string CategoryOfferUpdated = "offerUpdated";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryOfferLost = "offerLost";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryRequestExpired = "requestExpired";

    // Two categories the deleted in-gateway push stack served and D4 never named.
    // Human-addressed, so ShadeAndStored; mobile has no route for either yet, shade only.
    public const string CategoryAvailability = "availability";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryPromotion = "promotion";

    // D12: support/ticket case updates. Human-addressed, so ShadeAndStored, like `dispute`.
    public const string CategorySupport = "support";

    // A category maps to EXACTLY ONE mode. That single-valuedness IS the corollary's
    // enforcement: there is no way to express "this category is both", so no event can
    // legally produce a silent push and a stored push as two separate sends.
    private static readonly IReadOnlyDictionary<string, PushDeliveryMode> ModeByCategory =
        new Dictionary<string, PushDeliveryMode>(StringComparer.Ordinal)
        {
            // D4 · silent-only (pure refresh signal — the poll being replaced).
            // ONE entry, not two: `delivery` was here until the 2026-07-27 reversal.
            [CategoryNewRequest] = PushDeliveryMode.SilentRefresh,

            // D4 · shade + stored
            // `delivery` joined this block on 2026-07-27 (owner ruling: a delivery-status
            // change IS a readable inbox row). It is listed first so the reversal is
            // visible at the point of decision, not only in the doc comment.
            [CategoryDelivery] = PushDeliveryMode.ShadeAndStored,
            [CategoryKyc] = PushDeliveryMode.ShadeAndStored,
            [CategorySettlement] = PushDeliveryMode.ShadeAndStored,
            [CategoryDispute] = PushDeliveryMode.ShadeAndStored,
            [CategoryRating] = PushDeliveryMode.ShadeAndStored,
            [CategoryChat] = PushDeliveryMode.ShadeAndStored,

            // not named by D4 · shade + stored (behaviour-preserving, see above)
            [CategoryNewOffer] = PushDeliveryMode.ShadeAndStored,
            [CategoryOfferAccepted] = PushDeliveryMode.ShadeAndStored,
            [CategoryOfferUpdated] = PushDeliveryMode.ShadeAndStored,
            [CategoryOfferLost] = PushDeliveryMode.ShadeAndStored,
            [CategoryRequestExpired] = PushDeliveryMode.ShadeAndStored,

            // Migrated off the deleted in-gateway stack; both address a human.
            [CategoryAvailability] = PushDeliveryMode.ShadeAndStored,
            [CategoryPromotion] = PushDeliveryMode.ShadeAndStored,

            // D12: the case callback's support leg; `dispute` above covers its dispute leg.
            [CategorySupport] = PushDeliveryMode.ShadeAndStored,
        };

    // Notification TYPE (gateway-owned catalog template key) -> refresh category.
    // Every key in JeebNotificationCatalog must appear here; the guard test
    // PushSilencePolicyTests asserts that, so adding a catalog template without
    // deciding its mode fails the build's test gate rather than defaulting silently.
    private static readonly IReadOnlyDictionary<string, string> CategoryByTemplateKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jeeb.offer_received"] = CategoryNewOffer,
            ["jeeb.offer_accepted"] = CategoryOfferAccepted,
            ["jeeb.offer_updated"] = CategoryOfferUpdated,

            // "jeeb.offer_rejected" is deliberately ABSENT (b02 step 6b, owner ruling D3 =
            // retire): the notification centre 405s it, so it can never reach a centre writer and
            // this policy — which decides only whether to call the centre — has nothing to decide
            // about it. The loser-bidder push still ships, unconditionally, from
            // OfferPushNotifier.NotifyOfferLostAsync; CategoryOfferLost below stays because it is
            // the mobile-facing category on that push's wire payload.

            // ✅ CONTRADICTION RESOLVED BY THE OWNER, 2026-07-27. The history matters, so
            // it is recorded rather than erased:
            //   • D4 (2026-07-26) put `delivery` on the SILENT side — no centre row.
            //   • Work order 6a wanted a `jeeb.delivery_status_updated` centre writer whose
            //     DoD is "a readable row per type via GET /messages/receiver/{id}".
            // Those could not both hold, and the landmine guard
            // PushSilencePolicyTests.NoSilentClassifiedType_HasACentreWriteDto went RED the
            // moment step 6 added DeliveryStatusUpdatedNotificationRecord — which is
            // precisely what it was built to do. The owner then RULED: delivery IS a
            // readable inbox row (shade + stored). So CategoryDelivery moved to
            // ShadeAndStored above and this key keeps pointing at it.
            //
            // The guard was NOT weakened to get here. It still asserts "no silent type has
            // a centre-write DTO"; it passes now because delivery is no longer silent, not
            // because the assertion was relaxed. If you are tempted to move a type back to
            // the silent side, that DTO check will stop you, and it should.
            ["jeeb.delivery_status_updated"] = CategoryDelivery,

            ["jeeb.settlement_paid"] = CategorySettlement,
            ["jeeb.kyc_approved"] = CategoryKyc,
            ["jeeb.kyc_rejected"] = CategoryKyc,
            ["jeeb.dispute_resolved"] = CategoryDispute,
            ["jeeb.rating_auto_revealed"] = CategoryRating,
        };

    /// <summary>The full refresh-category taxonomy this policy decides over.</summary>
    public static IReadOnlyCollection<string> Categories =>
        (IReadOnlyCollection<string>)ModeByCategory.Keys;

    /// <summary>The notification types (catalog template keys) this policy decides over.</summary>
    public static IReadOnlyCollection<string> TemplateKeys =>
        (IReadOnlyCollection<string>)CategoryByTemplateKey.Keys;

    /// <summary>
    /// The one mode for a refresh category. Unknown categories are
    /// <see cref="PushDeliveryMode.ShadeAndStored"/>: an unrecognised category cannot be
    /// PROVEN to be a machine-addressed edge, and the two failure modes are not
    /// symmetric — a surplus inbox row is a visible, correctable cosmetic bug, whereas a
    /// wrongly-silenced human notification is invisible data loss.
    /// </summary>
    public static PushDeliveryMode ModeForCategory(string? category)
        => category is not null && ModeByCategory.TryGetValue(category, out var mode)
            ? mode
            : PushDeliveryMode.ShadeAndStored;

    /// <summary>The refresh category for a notification type, or null when unmapped.</summary>
    public static string? CategoryForTemplateKey(string? templateKey)
        => templateKey is not null && CategoryByTemplateKey.TryGetValue(templateKey, out var category)
            ? category
            : null;

    /// <summary>
    /// The one mode for a notification type. Same fail-visible default as
    /// <see cref="ModeForCategory"/> for an unmapped type.
    /// </summary>
    public static PushDeliveryMode ModeForTemplateKey(string? templateKey)
        => ModeForCategory(CategoryForTemplateKey(templateKey));

    /// <summary>
    /// True when this notification type is a silent refresh signal and therefore must
    /// produce NO notification-centre row.
    /// </summary>
    public static bool IsSilent(string? templateKey)
        => ModeForTemplateKey(templateKey) == PushDeliveryMode.SilentRefresh;
}
