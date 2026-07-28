using System;

namespace JeebGateway.Notifications;

/// <summary>
/// The ONE per-recipient budget every push seat in the gateway must use, and the
/// arithmetic for sizing a detached fan-out's ceiling from it.
///
/// <para><b>Why this type exists at all.</b> The number used to be a private
/// <c>PushTimeout</c> field copy-pasted into every notifier. JEBV4-345 measured the real
/// cost of a push to a REGISTERED recipient, found the 2s copies could never complete one,
/// raised the copy in <see cref="ChatMessagePushNotifier"/> to 10s — and left the other five
/// at 2s. A per-seat constant is a per-seat opportunity to drift, and it drifted the moment
/// it was first corrected. There is now one value; a new notifier that wants a different one
/// has to say so out loud.</para>
///
/// <para><b>The measured distribution behind 10s.</b> Re-measured on the dev host
/// 2026-07-28 against the live push service over loopback (no LAN in the path), POSITIVE
/// CONTROL FIRST so a hung endpoint could not be mistaken for a slow one:</para>
/// <list type="bullet">
///   <item><b>control</b> — a user id with NO device rows: <c>404</c> in
///   <b>13.9 / 15.7 / 14.0 ms</b>. The endpoint is not hanging, and the instrument is alive.
///   This is also the origin of the "the LAN-local push svc is normally &lt;200ms" claim the
///   2s cap was justified with: it describes a push that has nothing to deliver.</item>
///   <item><b>registered jeeber</b> (19 device rows, 4 accepted by FCM):
///   <b>3.019 / 2.667 / 2.567 / 2.571 / 2.532 s</b></item>
///   <item><b>registered customer</b> (24 device rows, 3 accepted by FCM):
///   <b>3.351 / 3.674 / 3.969 / 3.568 / 3.373 s</b></item>
/// </list>
/// <para>n=10 healthy calls: min <b>2.532s</b>, median <b>3.185s</b>, max <b>3.969s</b>.
/// <b>Ten out of ten exceeded 2s.</b> That is the whole defect in one number — a 2s cap does
/// not "bound a slow push service", it guarantees that a push to a recipient who actually has
/// a device is the ONLY kind that can never complete. The healthy path was the only path the
/// cap could not survive.</para>
///
/// <para><b>Why 10s and not 4s or 30s.</b> The cost is linear in the recipient's device-row
/// count (~170ms of FCM round trip per row, sequential), and rows accumulate — the push
/// service never deletes a row whose send failed, so the two live accounts already carry 19
/// and 24 rows of which 15 and 21 are dead. A cap sized to today's worst observed call
/// (3.969s) would be back under water at ~40 rows. 10s covers roughly 58 rows and is 2.5x the
/// worst observed call. The upper bound is not a preference either: the push HttpClient's own
/// resilience pipeline already times out at 10s
/// (<c>ServiceClientExtensions.ConfigurePushBreakerAndTimeout</c>), so anything above 10s here
/// is dead code the pipeline would pre-empt, and anything below it makes this CTS a second,
/// competing deadline in front of the transport's own. 10s is the only value that is both
/// above the measured healthy distribution and not in front of the transport.</para>
///
/// <para><b>The load-bearing consequence.</b> A budget this size may NOT sit in front of a
/// user-visible response. Raising a cap on an inline-awaited seat converts "the push is
/// silently cancelled" into "the customer's accept takes 10s x recipients", which is the
/// JEBV4-281 failure — the mobile client's receive timeout fires and the user is told "No
/// internet connection" about a state change that already committed. Every seat using this
/// value must therefore be detached from the request path; see
/// <see cref="IDetachedPushDispatcher"/> and <see cref="ForFanOut"/>.</para>
/// </summary>
public static class PushSendBudget
{
    /// <summary>
    /// Bounds ONE recipient's send. Per-recipient on purpose: a single deadline shared across
    /// a fan-out lets the first recipient's ~3s round trip leave the last one with nothing,
    /// and the last one composed is reliably the jeeber.
    /// </summary>
    public static readonly TimeSpan PerRecipient = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Head-room on top of the per-recipient budget for a DETACHED fan-out: DI scope
    /// creation, payload build, the log write. Small on purpose — the ceiling exists to stop
    /// a wedged background task living forever, not to add a second, competing deadline.
    /// </summary>
    public static readonly TimeSpan DispatchOverhead = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ceiling a detached fan-out may run for, sized from the number of recipients it
    /// actually has. A flat ceiling re-imposes the shared deadline
    /// <see cref="PerRecipient"/> exists to prevent, from outside the notifier, and the
    /// recipient it starves is always the LAST one composed.
    /// </summary>
    public static TimeSpan ForFanOut(int recipientCount)
        => DispatchOverhead + (PerRecipient * Math.Max(1, recipientCount));
}
