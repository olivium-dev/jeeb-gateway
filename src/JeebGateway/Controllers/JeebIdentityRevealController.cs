using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Ratings;
using JeebGateway.Requests;
using JeebGateway.Requests.IdentityReveal;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Controllers;

/// <summary>
/// DOUBLE-BLIND IDENTITY REVEAL BFF — feature <c>double-blind-reveal</c>.
///
/// <para>
/// <c>GET /v1/deliveries/{deliveryId}/counterpart</c> — returns the privacy-scoped
/// identity of the OTHER party on a delivery (the Jeeber to a customer caller; the
/// customer to a Jeeber caller), with the visible slice decided SERVER-SIDE by
/// <see cref="IdentityRevealPolicy"/> from the delivery's current lifecycle status.
/// </para>
///
/// <para>
/// This is the backing route the mobile delivery-status screen expects to populate
/// its <c>JeeberSummary</c> contact card. Per that contract: the gateway exposes a
/// short display name in flight, withholds the phone until the in-custody window,
/// and exposes the rating chip only when an identity may be shown. The mobile client
/// is NOT trusted to decide what to show — it renders whatever scoped fields come back.
/// </para>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): the controller authenticates, reads the delivery row
/// the gateway already mirrors (<see cref="IRequestsStore"/>), resolves the counterpart's
/// public profile via the user-management client, and shapes the response through the
/// pure policy. It holds NO state and contains NO domain mutation — only presentation
/// scoping the gateway legitimately owns. ADR-0004: it calls services (UM, rating store),
/// never a sibling controller.
/// </para>
/// </summary>
[ApiController]
[Route("v1/deliveries/{deliveryId}/counterpart")]
// ADR-005 L2 §E: dual-party delivery surface — coarse {client, jeeber} participation claim.
// WHICH party the caller is and what they may SEE is decided below by IdentityRevealPolicy
// against the row the caller is actually on (own-scoping); a non-party caller resolves to a
// counterpart id that does not match and is rejected 403.
[RequireCapability(Capabilities.DeliveryParticipate)]
public sealed class JeebIdentityRevealController : ControllerBase
{
    private readonly IRequestsStore _requests;
    private readonly IRatingStore _ratings;
    private readonly JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient _users;
    private readonly ILogger<JeebIdentityRevealController> _log;

    public JeebIdentityRevealController(
        IRequestsStore requests,
        IRatingStore ratings,
        JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient users,
        ILogger<JeebIdentityRevealController> log)
    {
        _requests = requests;
        _ratings = ratings;
        _users = users;
        _log = log;
    }

    /// <summary>
    /// GET /v1/deliveries/{deliveryId}/counterpart — the lifecycle-scoped identity of
    /// the caller's counterpart on this delivery.
    /// </summary>
    /// <returns>
    /// <list type="bullet">
    ///   <item>200 + <see cref="CounterpartIdentityDto"/> — fields populated per the reveal
    ///     level. At <c>Hidden</c> the identity fields are null and <c>revealLevel:"hidden"</c>.</item>
    ///   <item>401 when no caller identity is present.</item>
    ///   <item>403 when the caller is not a party on this delivery.</item>
    ///   <item>404 for an unknown delivery id.</item>
    /// </list>
    /// </returns>
    [HttpGet]
    [ProducesResponseType(typeof(CounterpartIdentityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCounterpart(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized))
            return unauthorized;

        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null) return NotFound();

        // Resolve which party the caller is, and therefore who the counterpart is.
        // Own-scoping: the caller must actually be on the row. A caller that is
        // neither the client nor the bound jeeber gets 403 — they cannot use this
        // route to deanonymize an unrelated delivery.
        var callerIsClient = string.Equals(delivery.ClientId, callerId, StringComparison.Ordinal);
        var callerIsJeeber = !string.IsNullOrEmpty(delivery.JeeberId)
                             && string.Equals(delivery.JeeberId, callerId, StringComparison.Ordinal);

        if (!callerIsClient && !callerIsJeeber)
        {
            return Problem(
                title: "Not a party on this delivery.",
                statusCode: StatusCodes.Status403Forbidden,
                type: "https://jeeb.dev/errors/identity-reveal-not-a-party");
        }

        var counterpartId = callerIsClient ? delivery.JeeberId : delivery.ClientId;
        var counterpartBound = !string.IsNullOrEmpty(counterpartId);

        // SERVER-OWNED reveal decision. Status in → level out. The client never
        // makes this call.
        var level = IdentityRevealPolicy.LevelFor(delivery.Status, counterpartBound);

        // Hidden: respond 200 with an explicit hidden envelope (NOT 404 / NOT a
        // populated body). The screen renders the placeholder contact card.
        if (level == IdentityRevealLevel.Hidden || !counterpartBound)
        {
            return Ok(CounterpartIdentityDto.Hidden(deliveryId));
        }

        // Resolve the counterpart's public profile. A profile lookup fault must
        // NOT leak more than the policy allows and must NOT hard-fail the screen —
        // degrade to a minimal name-preview envelope.
        string? username = null;
        string? avatarUrl = null;
        try
        {
            var profile = await _users.ProfileAsync(counterpartId!, ct);
            username = profile?.Username;
            avatarUrl = profile?.ProfilePic;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "identity-reveal: profile lookup failed for counterpart of delivery {DeliveryId}; degrading to minimal preview.",
                deliveryId);
        }

        var displayName = ToShortDisplayName(username);

        // Rating chip — only meaningful once a name may be shown, and only the
        // counterpart's revealed rating for THIS delivery (no cross-delivery
        // aggregation leak). Phone is exposed only at the contactable level.
        double? rating = null;
        var ratingPair = await _ratings.GetAsync(deliveryId, ct);
        if (ratingPair is not null)
        {
            // The counterpart's rating of the caller is the caller's "stars received";
            // for the contact card we surface the counterpart's own submitted score
            // when present (the value the screen shows next to their name).
            var counterpartEntry = callerIsClient ? ratingPair.JeeberRating : ratingPair.ClientRating;
            if (counterpartEntry is not null)
            {
                rating = Math.Round((double)counterpartEntry.Stars, 1);
            }
        }

        // Phone exposure (contactable window only):
        //  - Jeeber → recipient: surface the row's RecipientPhone so the courier
        //    can call the door at drop-off (this is exactly what RecipientPhone is
        //    for, JEB-55).
        //  - Client → Jeeber: the raw number is deliberately NOT exposed even when
        //    contactable — the client reaches the Jeeber through the masked-call
        //    channel (CallsController), per the anti-spam design. Phone stays null;
        //    the screen falls back to the masked-call CTA.
        var phone = IdentityRevealPolicy.IsPhoneVisible(level) && callerIsJeeber
            ? NormalizePhone(delivery.RecipientPhone)
            : null;

        return Ok(new CounterpartIdentityDto
        {
            DeliveryId = deliveryId,
            RevealLevel = ToWire(level),
            DisplayName = displayName,
            VehicleLabel = callerIsClient ? (delivery.TierId ?? "Vehicle") : "Customer",
            PhoneE164 = phone,
            AvatarUrl = avatarUrl,
            Rating = rating,
        });
    }

    /// <summary>
    /// Reduces a full username to the short "first-name + initial" form the
    /// in-flight contact card shows ("Karim H."). Never returns the full surname.
    /// </summary>
    private static string ToShortDisplayName(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "Jeeb user";
        var parts = username.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0];
        var initial = parts[^1].Length > 0 ? parts[^1][0].ToString().ToUpperInvariant() + "." : string.Empty;
        return $"{parts[0]} {initial}".Trim();
    }

    private static string? NormalizePhone(string? phone)
        => string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();

    private static string ToWire(IdentityRevealLevel level) => level switch
    {
        IdentityRevealLevel.Contactable => "contactable",
        IdentityRevealLevel.NamePreview => "name_preview",
        _ => "hidden",
    };
}

/// <summary>
/// Privacy-scoped counterpart identity the mobile delivery-status screen consumes
/// to populate its <c>JeeberSummary</c> contact card. Fields are null when the
/// current <see cref="RevealLevel"/> does not permit them — the wire shape is stable
/// across levels so the client never branches on missing keys.
/// </summary>
public sealed class CounterpartIdentityDto
{
    /// <summary>The delivery this identity slice belongs to.</summary>
    public string DeliveryId { get; init; } = string.Empty;

    /// <summary>"hidden" | "name_preview" | "contactable" — the server's verdict.</summary>
    public string RevealLevel { get; init; } = "hidden";

    /// <summary>Short first-name+initial. Null at the hidden level.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Human vehicle label ("Scooter"/"Car") or "Customer". Null when hidden.</summary>
    public string? VehicleLabel { get; init; }

    /// <summary>E.164 dialable number. Null unless the level is contactable.</summary>
    public string? PhoneE164 { get; init; }

    /// <summary>Signed avatar URL, or null.</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>Display rating (1–5, one decimal). Null hides the chip.</summary>
    public double? Rating { get; init; }

    /// <summary>The hidden envelope — stable shape, all identity fields null.</summary>
    public static CounterpartIdentityDto Hidden(string deliveryId) => new()
    {
        DeliveryId = deliveryId,
        RevealLevel = "hidden",
    };
}
