using System;
using System.Text.RegularExpressions;

namespace JeebGateway.Realtime;

/// <summary>
/// The one place that decides what a courier-position topic is called, so the publish
/// side and the descriptor side cannot drift apart.
///
/// <para>Topic <c>jeeb:delivery:{deliveryId}</c>, stream <c>location</c>. The stream
/// name is not decorative: <c>LiveComm.Throttle</c> keys its policy table by stream, and
/// <c>"location"</c> is a first-class entry there (<c>interval_ms: 1000</c>,
/// <c>distance_threshold_m: 5</c>), so positions published under this exact name are
/// coalesced by the service instead of by us.</para>
///
/// <para><b>The delivery id is sanitized, not trusted.</b> A realtime topic is
/// colon-delimited and the ACL matches <c>*</c> as a wildcard segment
/// (<c>LiveComm.Topics.Topic.matches?/2</c>). An id containing <c>:</c> or <c>*</c>
/// would let a caller widen or escape the namespace it was scoped to — so an id that
/// is not a plain <c>[A-Za-z0-9_-]+</c> token is refused outright rather than escaped.
/// Every delivery id the gateway issues is a GUID, which satisfies this.</para>
/// </summary>
public static class CourierPositionTopic
{
    /// <summary>Realtime stream name; must stay in step with the Throttle policy table.</summary>
    public const string Stream = "location";

    private const string Prefix = "jeeb:delivery:";

    // Deliberately excludes ':' and '*' — see the class remarks.
    private static readonly Regex SafeId = new(
        "^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The topic for a delivery, or <c>null</c> when the id cannot safely form one.
    /// </summary>
    public static string? For(string? deliveryId)
        => !string.IsNullOrWhiteSpace(deliveryId) && SafeId.IsMatch(deliveryId)
            ? Prefix + deliveryId
            : null;

    /// <summary>Whether an id is a safe topic segment.</summary>
    public static bool IsSafeDeliveryId(string? deliveryId)
        => !string.IsNullOrWhiteSpace(deliveryId) && SafeId.IsMatch(deliveryId);
}
