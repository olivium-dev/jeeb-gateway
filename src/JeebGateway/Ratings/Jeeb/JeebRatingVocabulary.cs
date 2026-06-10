using System;
using System.Collections.Generic;
using System.Linq;
using JeebGateway.service.ServiceFeedback;

namespace JeebGateway.Ratings.Jeeb;

/// <summary>
/// JEB-1489 (GR2) — the Jeeb-domain rating semantics that MUST live in the gateway
/// and never leak into the shared feedback-service. This is the single place that
/// knows the Jeeb vocabulary the generic two-party blind-rating primitive is
/// deliberately ignorant of:
///
/// <list type="bullet">
///   <item><b>Roles</b> — the Jeeb dual-party codenames <c>Sami</c> (the requesting
///     client) and <c>Kamal</c> (the fulfilling jeeber), per ADR-003.</item>
///   <item><b>Partition value</b> — the opaque <c>scopeKey</c>/partition value Jeeb
///     stamps onto the generic surface (<see cref="PartitionValue"/>).</item>
///   <item><b>Tag taxonomy</b> — the Jeeb-specific rating tag whitelist.</item>
///   <item><b>deliveryId &lt;-&gt; rating linkage</b> — Jeeb correlates a blind rating
///     to a delivery row via an opaque <c>correlationId</c> derived from the
///     deliveryId (<see cref="CorrelationForDelivery"/>).</item>
///   <item><b>generic -&gt; Jeeb projection</b> — mapping the shared
///     <see cref="BlindRevealStateResponse"/> into the Jeeb <c>{state, ...}</c>
///     shape (<see cref="JeebRatingProjection"/>).</item>
/// </list>
///
/// The shared feedback-service stores only opaque correlationId + rater/ratee +
/// generic tags[]; all of the above is applied here, in the BFF.
/// </summary>
public static class JeebRatingVocabulary
{
    /// <summary>
    /// The opaque partition/scope value Jeeb stamps onto the generic primitive's
    /// <c>tags[]</c> so Jeeb ratings are isolated from any other product that
    /// might one day share the feedback-service. The shared service treats this as
    /// an opaque string — it carries no meaning downstream.
    /// </summary>
    public const string PartitionValue = "jeeb";

    /// <summary>
    /// Prefix for the deliveryId -&gt; correlationId linkage. Kept Jeeb-side so the
    /// shared primitive's correlationId remains an opaque token.
    /// </summary>
    private const string CorrelationPrefix = "jeeb:delivery:";

    /// <summary>
    /// The Jeeb rating tag whitelist. Submissions may only carry tags from this
    /// taxonomy; everything else is rejected at the BFF before the opaque values
    /// reach the shared service.
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllowedTags = new[]
    {
        "punctuality",
        "communication",
        "package_condition",
        "courtesy",
        "navigation",
    };

    /// <summary>
    /// Derive the opaque correlationId for a delivery's mutual-blind rating. This
    /// is the deliveryId &lt;-&gt; rating linkage; it stays in the gateway.
    /// </summary>
    public static string CorrelationForDelivery(string deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
            throw new ArgumentException("deliveryId is required.", nameof(deliveryId));

        return CorrelationPrefix + deliveryId.Trim();
    }

    /// <summary>
    /// Map a caller's Jeeb role to the partition-stamped, validated tag set that is
    /// sent as the generic primitive's opaque <c>tags[]</c>. Always includes the
    /// <see cref="PartitionValue"/> partition tag and the role tag; any caller tags
    /// must be drawn from <see cref="AllowedTags"/>.
    /// </summary>
    public static List<string> BuildTags(JeebRatingRole role, IEnumerable<string>? requestedTags)
    {
        var tags = new List<string> { PartitionValue, RoleTag(role) };

        if (requestedTags != null)
        {
            foreach (var raw in requestedTags)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var tag = raw.Trim().ToLowerInvariant();
                if (!AllowedTags.Contains(tag))
                    throw new ArgumentException($"'{raw}' is not a recognised Jeeb rating tag.", nameof(requestedTags));
                if (!tags.Contains(tag)) tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>The per-role tag stamped alongside the partition value.</summary>
    public static string RoleTag(JeebRatingRole role) => role switch
    {
        JeebRatingRole.Sami => "role:sami",
        JeebRatingRole.Kamal => "role:kamal",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>
    /// Resolve the caller's Jeeb role from the delivery party comparison. Sami is
    /// the requesting client; Kamal is the fulfilling jeeber.
    /// </summary>
    public static JeebRatingRole RoleFor(bool callerIsClient)
        => callerIsClient ? JeebRatingRole.Sami : JeebRatingRole.Kamal;
}

/// <summary>
/// The Jeeb dual-party rating roles (ADR-003 codenames). <c>Sami</c> is the
/// requesting client; <c>Kamal</c> is the fulfilling jeeber. This vocabulary is
/// Jeeb-domain and stays in the gateway (GR2).
/// </summary>
public enum JeebRatingRole
{
    /// <summary>The requesting client.</summary>
    Sami,

    /// <summary>The fulfilling jeeber.</summary>
    Kamal,
}
