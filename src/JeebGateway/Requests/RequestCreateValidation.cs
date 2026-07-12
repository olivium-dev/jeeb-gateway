using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Requests;

/// <summary>
/// JEBV4-65 (contract-audit finding 7) — single source of truth for
/// request-create field validation. The description-required, tier-exists,
/// initial-status-legality, and audio/photo URL-shape + photo-count checks were
/// hand-rolled in THREE create surfaces (RequestsController legacy <c>/requests</c>,
/// RequestVoiceController <c>/v1/requests</c> multipart, and
/// V1/JeebRequestsController <c>/v1/requests</c> JSON) with nothing enforcing
/// agreement — the mechanism that let the same concern drift across copies
/// (same failure shape as the route collision in JEBV4-61).
///
/// Each method returns a <see cref="ProblemDetails"/> describing the first
/// violation — with <c>Status</c> and <c>Type</c> set EXACTLY as the callers
/// produced them (behaviour-preserving) — or <c>null</c> when valid. The caller
/// wraps the result in the matching typed result (BadRequest/NotFound/
/// UnprocessableEntity) so the HTTP shape is byte-identical to before.
///
/// This is also the COUPLING POINT for JEBV4-62: the tier-not-found status is
/// defined once here, so changing it moves all three surfaces atomically instead
/// of leaving three copies to drift.
/// </summary>
public static class RequestCreateValidation
{
    /// <summary>T-backend-007: MVP cap on attached photos per request.</summary>
    public const int MaxPhotos = 10;

    /// <summary>
    /// audio_url / photos[] entries must look like absolute URLs. Mirrors the DB
    /// CHECK <c>delivery_requests_audio_url_format</c> in migration 0004 —
    /// gateway-side validation so a bad URL never reaches the store.
    /// </summary>
    private static readonly Regex UrlShape =
        new(@"^(https?|s3)://[^\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// JEB-45 (S05 N5): the only INITIAL statuses a create may legally land on.
    /// The server picks <c>pending</c> (immediate) or <c>scheduled</c> (when a
    /// future <c>scheduledAt</c> is supplied); any other client-supplied status is
    /// an illegal initial transition (422). Compared case-insensitively.
    /// </summary>
    public static readonly IReadOnlySet<string> LegalInitialStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RequestStatus.Pending,
            RequestStatus.Scheduled,
        };

    /// <summary>
    /// description-required → 400 (no Type URI; matches the legacy and JSON
    /// surfaces). The caller keeps its own <c>body is null</c> / whitespace guard
    /// (for C# null-flow narrowing) and returns this envelope via
    /// <c>BadRequest(...)</c>.
    /// </summary>
    public static ProblemDetails DescriptionRequiredProblem() => new()
    {
        Title = "description is required.",
        Status = StatusCodes.Status400BadRequest,
    };

    /// <summary>
    /// initial-status legality → 422 transition-not-allowed. A supplied status
    /// that is not a legal INITIAL state is rejected; a legal value (or none) is a
    /// no-op. Returns null when valid.
    /// </summary>
    public static ProblemDetails? ValidateInitialStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || LegalInitialStatuses.Contains(status))
        {
            return null;
        }

        return new ProblemDetails
        {
            Title = "Illegal initial status for a new request.",
            Detail = $"A request may only be created in 'pending' or 'scheduled'; got '{status}'.",
            Status = StatusCodes.Status422UnprocessableEntity,
            Type = "https://jeeb.dev/errors/transition-not-allowed",
        };
    }

    /// <summary>
    /// tier-exists → 404 tier-not-found. <paramref name="fieldLabel"/> is the
    /// surface's field name — <c>"tierId"</c> (legacy/JSON) or <c>"tier"</c> (voice
    /// form) — so each surface keeps its exact Title/Detail wording; Status + Type
    /// are identical across all three (the JEBV4-62 coupling point). Returns null
    /// when the tier exists.
    /// </summary>
    public static async Task<ProblemDetails?> ValidateTierExistsAsync(
        ITiersStore tiers, string tierId, string fieldLabel, CancellationToken ct)
    {
        if (await tiers.ExistsAsync(tierId, ct))
        {
            return null;
        }

        return new ProblemDetails
        {
            Title = $"{fieldLabel} does not match any active delivery tier.",
            Detail = $"{fieldLabel}={tierId}",
            Status = StatusCodes.Status404NotFound,
            Type = "https://jeeb.dev/errors/tier-not-found",
        };
    }

    /// <summary>
    /// audio/photo URL-shape + photo-count → 400. Preserves the exact original
    /// order and envelopes: audio-url-invalid, then photos-too-many, then
    /// photo-url-invalid. Returns null when all URLs/counts are valid.
    /// </summary>
    public static ProblemDetails? ValidateUrlAndPhotos(string? audioUrl, IReadOnlyCollection<string> photos)
    {
        if (!string.IsNullOrEmpty(audioUrl) && !UrlShape.IsMatch(audioUrl))
        {
            return new ProblemDetails
            {
                Title = "audioUrl must be an absolute http(s):// or s3:// URL.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/audio-url-invalid",
            };
        }

        if (photos.Count > MaxPhotos)
        {
            return new ProblemDetails
            {
                Title = $"Too many photos attached (max {MaxPhotos}).",
                Detail = $"received={photos.Count}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/photos-too-many",
            };
        }

        foreach (var photo in photos)
        {
            if (string.IsNullOrWhiteSpace(photo) || !UrlShape.IsMatch(photo))
            {
                return new ProblemDetails
                {
                    Title = "Every photos[] entry must be an absolute http(s):// or s3:// URL.",
                    Status = StatusCodes.Status400BadRequest,
                    Type = "https://jeeb.dev/errors/photo-url-invalid",
                };
            }
        }

        return null;
    }
}
