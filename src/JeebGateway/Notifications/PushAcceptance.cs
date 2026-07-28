using System;
using System.Globalization;
using System.Text.RegularExpressions;
using JeebGateway.service.ServicePushNotification;

namespace JeebGateway.Notifications;

/// <summary>
/// What the push service's 201 actually means, made legible to the log.
///
/// <para><b>The lie this replaces.</b> Every push seat used to log
/// <c>"... push ACCEPTED by push service for recipient {X}"</c> on a 2xx and throw the
/// response body away. A reader — and three consecutive investigations did read it this way —
/// takes "ACCEPTED" to mean the recipient got the notification. It does not. The per-user
/// endpoint walks EVERY device row registered for that user, calls FCM once per row, counts
/// the outcomes, and returns 201 if <b>at least one row</b> was accepted; the real numbers are
/// in the response body and nowhere else. Measured live on the dev host 2026-07-28, the two
/// live accounts answered 201 carrying <c>"sent successfully to 4 device(s) ... (15 failed)"</c>
/// and <c>"3 device(s) ... (21 failed)"</c>. So the word "ACCEPTED" was standing in for
/// "FCM took 4 of this user's 19 registered tokens, and I am not going to tell you whether the
/// phone in your hand was one of them."</para>
///
/// <para><b>Why that matters beyond tidiness.</b> A success line emitted for something that
/// did not demonstrably happen is the same failure mode as a refund client returning success
/// for money that never moved: it does not just fail to help, it actively terminates the
/// investigation that would have found the real fault. This project has an explicit ruling
/// about that shape. The fix is to log the accounting the push service already computed, and
/// to name the residual honestly — see <see cref="Describe"/>.</para>
///
/// <para><b>What this still cannot tell you.</b> Which physical device. The push service's
/// per-token loop swallows the per-row exception with no log and no row delete
/// (<c>failed_count += 1; continue</c>), so "was the live handset among the failures?" is
/// unanswerable from either side today. Fixing that is a change to the shared push service and
/// needs its owner; this type is the gateway half — it stops the gateway from claiming more
/// than it knows.</para>
/// </summary>
public static class PushAcceptance
{
    /// <summary>Matches the accepted-device count in the push service's success message.</summary>
    private static readonly Regex AcceptedRx = new(
        @"to\s+(\d+)\s+device", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Matches the trailing failed-device count, which is omitted when it is zero.</summary>
    private static readonly Regex FailedRx = new(
        @"\((\d+)\s+failed\)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Upper bound on the raw message echoed when the shape is unrecognised.</summary>
    private const int MaxRawLength = 200;

    /// <summary>
    /// The device-row accounting behind one 2xx from the per-user send endpoint.
    /// <paramref name="Accepted"/>/<paramref name="Failed"/> are <c>null</c> when the message
    /// did not carry them (an older/changed push-service build) — <c>null</c> means
    /// "unknown", never "zero".
    /// </summary>
    public readonly record struct Result(int? Accepted, int? Failed, string Raw)
    {
        /// <summary>Device rows the push service reported, when both halves parsed.</summary>
        public int? Total => Accepted is int a && Failed is int f ? a + f : null;
    }

    /// <summary>Parses the push service's success message into its device-row counts.</summary>
    public static Result Parse(SentPayloadResponse? response)
    {
        var raw = response?.Message ?? string.Empty;
        if (raw.Length > MaxRawLength)
        {
            raw = raw.Substring(0, MaxRawLength) + "…";
        }

        return new Result(Capture(AcceptedRx, response?.Message), Capture(FailedRx, response?.Message), raw);
    }

    /// <summary>
    /// A log-safe rendering of what the push service actually reported.
    ///
    /// <para>Deliberately worded so it CANNOT be read as delivery. "fcmAccepted=4/19" is a
    /// statement about tokens FCM took off the push service's hands; the handset is downstream
    /// of that and is not in evidence here. When the counts cannot be parsed the raw message is
    /// echoed rather than silently rendering a confident-looking zero.</para>
    /// </summary>
    public static string Describe(SentPayloadResponse? response)
    {
        var parsed = Parse(response);

        if (parsed.Accepted is not int accepted)
        {
            return string.IsNullOrWhiteSpace(parsed.Raw)
                ? "fcmAccepted=unknown (push service returned no accounting)"
                : "fcmAccepted=unknown detail=\"" + parsed.Raw + "\"";
        }

        var total = parsed.Total;
        var scope = total is int t
            ? accepted.ToString(CultureInfo.InvariantCulture) + "/" + t.ToString(CultureInfo.InvariantCulture)
            : accepted.ToString(CultureInfo.InvariantCulture);

        return "fcmAccepted=" + scope + " deviceRows"
               + (parsed.Failed is int failed && failed > 0
                   ? " fcmRejected=" + failed.ToString(CultureInfo.InvariantCulture)
                   : string.Empty);
    }

    private static int? Capture(Regex rx, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var m = rx.Match(message);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }
}
