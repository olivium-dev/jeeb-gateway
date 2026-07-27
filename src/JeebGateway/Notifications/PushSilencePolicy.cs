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
/// (<c>:10026</c>) owns the stored rows, which is why the only decision made here is
/// whether to call it at all.</para>
///
/// <para><b>⚠️ REACHABILITY — READ THIS BEFORE BELIEVING THE TESTS.</b> As of this commit
/// the silent branch is <b>unreachable from any live caller</b>, and that is a fact about
/// the codebase, not a defect in this policy. <see cref="NotificationRecordWriter"/> has
/// exactly two public writer methods (<c>jeeb.offer_received</c>,
/// <c>jeeb.offer_accepted</c>) and both are <see cref="PushDeliveryMode.ShadeAndStored"/>.
/// The one silent-classified type, <c>jeeb.delivery_status_updated</c>, has no writer at
/// all, and <see cref="CategoryNewRequest"/> has no template key. So this policy changes
/// <b>zero</b> production behaviour today: it is a guard pre-placed at the sole choke point
/// ahead of the writers that will need it. Do not describe a green test suite here as proof
/// that a silent push was suppressed in production — nothing has yet asked for one.</para>
///
/// <para><b>⚠️ TWO DEFINITIONS OF "SILENT" ARE IN FLIGHT, and nothing reconciles them.</b>
/// This policy decides silence from the notification <b>type</b> (a static lookup). The
/// push service decides it from a per-send wire flag (<c>payload.get("silent")</c>, b02
/// step 2). They can disagree and no code forces agreement — today they never meet, because
/// nothing in this gateway stamps <c>silent</c> on an outbound payload (verified by
/// <c>grep -rni silent src --include=*.cs</c>: no hit in any send path). When the gateway
/// does start stamping it, both desync directions are live: a silent push that still wrote
/// a row (an inbox entry the user was never told about), or a shade buzz with no row (a
/// banner the user taps and finds nothing behind). Whoever wires the stamp must source it
/// from <see cref="IsSilent"/> and from nowhere else.</para>
///
/// <para><b>⚠️ THE FLAT WIRE <c>category</c> FIELD IS NOT A REFRESH CATEGORY.</b> Today's
/// payloads stamp <c>["category"] = "delivery"</c> on new-offer
/// (<c>OfferPushNotifier.cs</c>), new-request (<c>NewRequestPushNotifier.cs</c>) and
/// request-expiry (<c>DispatchingRequestExpiryNotifier.cs</c>) alike — it is a coarse
/// legacy product-area label, not the D4 taxonomy below. Feeding it to
/// <see cref="ModeForCategory"/> would silence the offer notifications, which are the two
/// notification types that DO have live centre writers. Resolve the mode from the
/// notification TYPE (template key), never from that field.</para>
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
    /// category is declared because D4 names it, not because it is wired. Both D4 silent
    /// categories are in this state today — see the class remarks.</para>
    /// </summary>
    public const string CategoryNewRequest = "newRequest";

    /// <summary>Silent-only per D4 — the delivery-status refresh signal.</summary>
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
    // These four are the offer-lifecycle events the gateway already emits. They are
    // human-addressed ("a jeeber bid on your request", "your offer was accepted",
    // "your offer wasn't selected", "your request found no coverage"), and two of them
    // already have live notification-centre writers. Classifying them ShadeAndStored is
    // therefore BEHAVIOUR-PRESERVING, and it is recorded as such — it is NOT an owner
    // ruling. Do not read the D4 rows above and these rows as equally authoritative.

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryNewOffer = "newOffer";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryOfferAccepted = "offerAccepted";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryOfferLost = "offerLost";

    /// <summary>Not named by D4; ShadeAndStored (behaviour-preserving).</summary>
    public const string CategoryRequestExpired = "requestExpired";

    // A category maps to EXACTLY ONE mode. That single-valuedness IS the corollary's
    // enforcement: there is no way to express "this category is both", so no event can
    // legally produce a silent push and a stored push as two separate sends.
    private static readonly IReadOnlyDictionary<string, PushDeliveryMode> ModeByCategory =
        new Dictionary<string, PushDeliveryMode>(StringComparer.Ordinal)
        {
            // D4 · silent-only (pure refresh signals — the polls being replaced)
            [CategoryNewRequest] = PushDeliveryMode.SilentRefresh,
            [CategoryDelivery] = PushDeliveryMode.SilentRefresh,

            // D4 · shade + stored
            [CategoryKyc] = PushDeliveryMode.ShadeAndStored,
            [CategorySettlement] = PushDeliveryMode.ShadeAndStored,
            [CategoryDispute] = PushDeliveryMode.ShadeAndStored,
            [CategoryRating] = PushDeliveryMode.ShadeAndStored,
            [CategoryChat] = PushDeliveryMode.ShadeAndStored,

            // not named by D4 · shade + stored (behaviour-preserving, see above)
            [CategoryNewOffer] = PushDeliveryMode.ShadeAndStored,
            [CategoryOfferAccepted] = PushDeliveryMode.ShadeAndStored,
            [CategoryOfferLost] = PushDeliveryMode.ShadeAndStored,
            [CategoryRequestExpired] = PushDeliveryMode.ShadeAndStored,
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
            ["jeeb.offer_rejected"] = CategoryOfferLost,

            // ⚠️ UNRESOLVED CONTRADICTION — OWNER DECISION REQUIRED, DO NOT SETTLE IT HERE.
            //   • Owner ruling D4 (2026-07-26) puts `delivery` on the SILENT side: a
            //     delivery-status change is the refresh signal that replaces the
            //     delivery-status poll, so it gets NO centre row.
            //   • Work order 6a wants a `jeeb.delivery_status_updated` centre writer whose
            //     DoD is "a readable row per type via GET /messages/receiver/{id}".
            // Those cannot both hold. This file encodes D4 because D4 is the ruling; that
            // makes 6a's DoD unsatisfiable for this type, and a 6a writer would emit rows
            // the gate then discards.
            // It is INERT today (nothing writes this type), so it is a landmine rather than
            // a live bug — which is exactly why it is enforced instead of merely commented:
            // PushSilencePolicyTests.NoSilentClassifiedType_HasACentreWriteDto goes RED the
            // moment a writer DTO appears for a silent type. Do not "fix" that red by
            // deleting the test or flipping this row; get the ruling.
            // If a specific delivery MILESTONE is judged worth telling the human about, the
            // corollary above says that is ONE non-silent notification of its OWN type
            // carrying `delivery` in its data block — NOT a second, silent push.
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
