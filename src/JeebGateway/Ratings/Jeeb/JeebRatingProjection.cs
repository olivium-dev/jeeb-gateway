using System;
using System.Collections.Generic;
using System.Linq;
using JeebGateway.service.ServiceFeedback;

namespace JeebGateway.Ratings.Jeeb;

/// <summary>
/// JEB-1489 (GR2) — the generic -&gt; Jeeb <c>{state, ...}</c> projection. Maps the
/// shared, product-agnostic <see cref="BlindRevealStateResponse"/> returned by the
/// feedback-service blind-rating primitive into the Jeeb-domain reveal shape the
/// mobile app expects (the same <c>pending_mine</c> / <c>pending_theirs</c> /
/// <c>revealed</c> state codes the legacy in-gateway path uses).
///
/// This projection is pure and side-effect free so it can be unit-tested without
/// HTTP/DI, exactly like <see cref="BlindRevealPolicy"/>.
/// </summary>
public static class JeebRatingProjection
{
    /// <summary>
    /// Project the generic upstream reveal state for a given delivery + caller role.
    /// </summary>
    public static JeebRatingStatusResponse Project(
        string deliveryId,
        JeebRatingRole callerRole,
        BlindRevealStateResponse upstream)
    {
        if (upstream is null) throw new ArgumentNullException(nameof(upstream));

        var self = upstream.Self;
        var theirs = upstream.Counterparty;
        var mutuallyRevealed = IsMutuallyRevealed(upstream);

        return new JeebRatingStatusResponse
        {
            DeliveryId = deliveryId,
            Role = callerRole == JeebRatingRole.Sami ? "sami" : "kamal",
            State = StateCode(upstream),
            Revealed = mutuallyRevealed,
            RevealedAt = mutuallyRevealed ? upstream.RevealedAt : null,
            Mine = ToParty(self, mutuallyRevealed),
            Theirs = ToParty(theirs, mutuallyRevealed),
        };
    }

    /// <summary>
    /// Generic -&gt; Jeeb state mapping. The shared primitive models only the
    /// two-party submit/reveal lattice (it deliberately has NO Jeeb 7-day window
    /// concept — that legality stays Jeeb-side), so this maps:
    /// <list type="bullet">
    ///   <item>both submitted -&gt; <c>revealed</c></item>
    ///   <item>only the viewer submitted -&gt; <c>pending_theirs</c></item>
    ///   <item>viewer has not submitted -&gt; <c>pending_mine</c></item>
    /// </list>
    /// </summary>
    public static string StateCode(BlindRevealStateResponse upstream)
    {
        if (upstream.Revealed)
            return IsMutuallyRevealed(upstream) ? RatingStateCodes.Revealed : RatingStateCodes.LockedNoRating;

        var selfSubmitted = upstream.Self?.Submitted == true;
        return selfSubmitted ? RatingStateCodes.PendingTheirs : RatingStateCodes.PendingMine;
    }

    private static bool IsMutuallyRevealed(BlindRevealStateResponse upstream)
    {
        var selfSubmitted = upstream.Self?.Submitted == true;
        var theirsSubmitted = upstream.Counterparty?.Submitted == true;
        var mutuallySubmitted = selfSubmitted && theirsSubmitted;
        var submittedCountComplete = upstream.SubmittedCount >= 2;

        return upstream.Revealed && submittedCountComplete && mutuallySubmitted;
    }

    private static JeebRatingPartyView ToParty(BlindRatingPartyState party, bool revealDetails)
    {
        if (party is null || !party.Submitted)
        {
            return new JeebRatingPartyView { Submitted = false };
        }

        if (!revealDetails)
        {
            return new JeebRatingPartyView
            {
                Submitted = true,
                SubmittedAt = party.SubmittedAt,
            };
        }

        return new JeebRatingPartyView
        {
            Submitted = true,
            Stars = party.Score,
            Comment = party.Comment,
            Tags = party.Tags?.ToList(),
            SubmittedAt = party.SubmittedAt,
        };
    }
}

/// <summary>
/// POST /v1/ratings/jeeb/deliveries/{deliveryId} body — the Jeeb-domain submit
/// shape. The gateway maps this onto the generic opaque primitive (correlationId
/// + rater/ratee + partition-stamped tags) before calling the shared service.
/// </summary>
public sealed class JeebSubmitRatingRequest
{
    public int Stars { get; set; }
    public string? Comment { get; set; }

    /// <summary>Optional Jeeb tag taxonomy values (see <see cref="JeebRatingVocabulary.AllowedTags"/>).</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>One party's Jeeb rating view. Counterparty fields stay null while blind.</summary>
public sealed class JeebRatingPartyView
{
    public bool Submitted { get; set; }
    public int? Stars { get; set; }
    public string? Comment { get; set; }
    public List<string>? Tags { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
}

/// <summary>
/// GET/POST /v1/ratings/jeeb/deliveries/{deliveryId} response — the Jeeb
/// <c>{state, ...}</c> projection of the generic reveal state.
/// </summary>
public sealed class JeebRatingStatusResponse
{
    public required string DeliveryId { get; init; }

    /// <summary>The caller's Jeeb role — <c>sami</c> (client) or <c>kamal</c> (jeeber).</summary>
    public required string Role { get; init; }

    /// <summary>One of <c>pending_mine</c>, <c>pending_theirs</c>, <c>revealed</c>.</summary>
    public required string State { get; init; }

    public bool Revealed { get; init; }
    public DateTimeOffset? RevealedAt { get; init; }

    public required JeebRatingPartyView Mine { get; init; }
    public required JeebRatingPartyView Theirs { get; init; }
}
