using System;
using System.Collections.Generic;

namespace JeebGateway.Conversations.Offers;

/// <summary>
/// JEB-1488 (correction #3 / GR2/GR3) — the canonical, gateway-owned builder and
/// validator for the Jeeb <b>structured-offer envelope</b> that is appended to a
/// conversation as an opaque structured message.
///
/// <para>
/// The offer vocabulary (the <c>jeeb.offer*</c> subtypes) and ALL offer/settlement
/// validation live HERE, in jeeb-gateway, and ONLY here. chat-service receives the
/// resulting <see cref="Payload"/> as an OPAQUE structured payload (a JSON object it
/// stores and round-trips verbatim) and applies NO offer- or settlement-aware
/// validation — see chat-service's <c>OpaqueOfferEnvelopeTests</c>. GR3 is honoured:
/// the envelope carries the offer terms the client quoted but NO settlement math
/// (no commission/payout computation) — settlement stays on the UPG path.
/// </para>
///
/// <para>
/// This type is intentionally additive and non-breaking (GR1): it does not alter the
/// existing verbatim message-append path (the gateway BFF forwards the client-built
/// envelope opaquely). It is the explicit, unit-tested home the boundary tests pin
/// so that offer-envelope construction/validation can never silently migrate into the
/// shared chat-service.
/// </para>
/// </summary>
public sealed class JeebOfferEnvelope
{
    /// <summary>The structured-offer message subtypes — Jeeb vocabulary, gateway-only.</summary>
    public const string SubtypeOffer = "jeeb.offer";
    public const string SubtypeOfferAccepted = "jeeb.offer_accepted";
    public const string SubtypeOfferRejected = "jeeb.offer_rejected";

    /// <summary>Structured messages are appended with this coarse kind.</summary>
    public const string KindStructured = "structured";

    /// <summary>
    /// Minimum gross fee (Client currency). Mirrors
    /// <c>RequestOffersController.MinimumFee</c> — the offer floor is a gateway rule.
    /// </summary>
    public const decimal MinimumFee = 1m;

    /// <summary>Note ceiling. Mirrors <c>RequestOffersController.MaxNoteLength</c>.</summary>
    public const int MaxNoteLength = 500;

    public string Subtype { get; private set; } = SubtypeOffer;

    public string OfferId { get; private set; } = string.Empty;

    public decimal PriceAmount { get; private set; }

    public int EtaMinutes { get; private set; }

    public string? Note { get; private set; }

    /// <summary>
    /// The opaque structured payload object that is forwarded to chat-service. It is a
    /// plain property bag — no settlement math, no Jeeb role names — that chat-service
    /// stores verbatim. Built ONLY here.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Payload { get; private set; }
        = new Dictionary<string, object?>();

    /// <summary>True iff <paramref name="subtype"/> is a gateway-owned offer subtype.</summary>
    public static bool IsOfferSubtype(string? subtype) =>
        string.Equals(subtype, SubtypeOffer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(subtype, SubtypeOfferAccepted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(subtype, SubtypeOfferRejected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build and VALIDATE a structured-offer envelope in the gateway. Throws
    /// <see cref="JeebOfferEnvelopeValidationException"/> when the offer terms are
    /// invalid (empty offer id, fee below the floor, non-positive ETA, over-long
    /// note). The returned envelope's <see cref="Payload"/> is the opaque object the
    /// gateway forwards to chat-service.
    /// </summary>
    public static JeebOfferEnvelope Build(
        string offerId,
        decimal priceAmount,
        int etaMinutes,
        string? note = null,
        string subtype = SubtypeOffer)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(offerId)) errors.Add("offer_id is required.");
        if (!IsOfferSubtype(subtype)) errors.Add($"'{subtype}' is not a recognised offer subtype.");
        if (priceAmount < MinimumFee) errors.Add($"fee must be at least {MinimumFee}.");
        if (etaMinutes <= 0) errors.Add("eta_minutes must be positive.");
        if (note is { Length: > MaxNoteLength }) errors.Add($"note exceeds {MaxNoteLength} characters.");

        if (errors.Count > 0)
        {
            throw new JeebOfferEnvelopeValidationException(errors);
        }

        return new JeebOfferEnvelope
        {
            Subtype = subtype,
            OfferId = offerId,
            PriceAmount = priceAmount,
            EtaMinutes = etaMinutes,
            Note = note,
            Payload = new Dictionary<string, object?>
            {
                ["offer_id"] = offerId,
                ["price_amount"] = priceAmount,
                ["eta_minutes"] = etaMinutes,
                ["note"] = note,
            },
        };
    }

    private JeebOfferEnvelope() { }
}

/// <summary>
/// Raised when <see cref="JeebOfferEnvelope.Build"/> rejects invalid offer terms.
/// Surfaced as a 400 by the gateway — never forwarded to chat-service.
/// </summary>
public sealed class JeebOfferEnvelopeValidationException : Exception
{
    public JeebOfferEnvelopeValidationException(IReadOnlyList<string> errors)
        : base("Invalid structured-offer envelope: " + string.Join(" ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
