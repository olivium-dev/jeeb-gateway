using System.Net;
using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Kyc;
using JeebGateway.Services;
using JeebGateway.Services.Cdn;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-005 / JEEB-23 / S03 H7-H8 / ADR-0004: admin KYC moderation queue +
/// review. This controller is a THIN BFF over the KYC domain seam
/// (<see cref="IKycBffSeam"/>): it composes the KYC review DECISION (kyc-service
/// when live, interim store while the Kyc flag is off) with the identity mutation
/// in user-management. It holds NO KYC state itself.
///
/// <para><b>The only identity-mutating transition (CP-C / H8).</b> On
/// <c>approve</c> the seam returns the role-grant INTENT
/// (<see cref="KycBffReviewResult.GrantsRole"/> = the opaque jeeber role); the
/// GATEWAY then composes the user-management append (jsonb <c>available_roles</c>,
/// set-semantics) + token re-issue. kyc-service NEVER calls user-management
/// (ARCH LAW). That composition lives in <see cref="KycAdminReviewComposer"/>, shared
/// verbatim with the CMS-compat review route so the two can never fork.</para>
///
/// <list type="bullet">
///   <item>GET <c>/admin/kyc/queue</c> — pending submissions oldest-first (H7/N6),
///     optionally filtered by <c>?q=</c> on the applicant's name/phone.</item>
///   <item>PATCH <c>/admin/kyc/{id}/review</c> — approve | reject | request_resubmit;
///     re-review of a finalised row → 409 (N8); RFC7807 throughout.</item>
/// </list>
/// </summary>
[ApiController]
[Route("admin/kyc")]
// ADR-005 L2: both the queue read and the review decision are the same admin capability
// (kyc.review), declared class-level (replaces class [RequireRole(Roles.Admin)]). Authorized
// purely from the 'admin' role claim. The KYC-approve identity mutation (UM append + token
// re-issue) is a downstream/STATE concern and stays unchanged in the action body.
public class AdminKycController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly KycQueueSearch _queue;
    private readonly KycAdminReviewComposer _reviews;
    private readonly IKycBffSeam _seam;
    private readonly IUsersStore _users;
    private readonly KycEvidenceTokenService _evidenceTokens;
    private readonly IHttpClientFactory _clients;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<AdminKycController> _log;

    // The three admin-viewable slots and the submission ref each resolves to.
    private static readonly IReadOnlyList<(string Slot, Func<KycBffSubmissionView, string?> Ref)> EvidenceSlots =
        new (string, Func<KycBffSubmissionView, string?>)[]
        {
            ("id-front", v => v.IdFrontRef),
            ("id-back", v => v.IdBackRef),
            ("selfie", v => v.SelfieRef),
        };

    public AdminKycController(
        KycQueueSearch queue,
        KycAdminReviewComposer reviews,
        IKycBffSeam seam,
        IUsersStore users,
        KycEvidenceTokenService evidenceTokens,
        IHttpClientFactory clients,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<AdminKycController> log)
    {
        _queue = queue;
        _reviews = reviews;
        _seam = seam;
        _users = users;
        _evidenceTokens = evidenceTokens;
        _clients = clients;
        _flags = flags;
        _log = log;
    }

    [HttpGet("queue")]
    [RequireCapability(Capabilities.KycReview)]
    [ProducesResponseType(typeof(KycQueueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Queue(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "page must be >= 1.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"pageSize must be between 1 and {MaxPageSize}.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        KycQueueSearchPage queue;
        try
        {
            queue = await _queue.SearchAsync(page, pageSize, q, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        return Ok(new KycQueueResponse
        {
            Items = queue.Items.Select(ToQueueItem).ToList(),
            Page = queue.Page,
            PageSize = queue.PageSize,
            Total = queue.Total
        });
    }

    /// <summary>
    /// GET /admin/kyc/{id} — the full admin review surface for one submission:
    /// the kyc-service fields, the applicant name from the gateway user projection,
    /// and self-authorizing tokenized image URLs the CMS renders directly in
    /// <c>&lt;img&gt;</c> tags. Each token is short-lived and bound to {id, slot};
    /// only a caller that passed kyc.review here can obtain one.
    /// </summary>
    [HttpGet("{id}")]
    [RequireCapability(Capabilities.KycReview)]
    [ProducesResponseType(typeof(KycSubmissionDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Detail(string id, CancellationToken ct)
    {
        KycBffSubmissionView? view;
        try
        {
            view = await _seam.GetByIdAsync(id, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        if (view is null) return NotFound();

        string? userName = null;
        if (!string.IsNullOrWhiteSpace(view.UserId))
        {
            try
            {
                var profile = await _users.GetByIdAsync(view.UserId, ct);
                userName = string.IsNullOrWhiteSpace(profile?.Name) ? null : profile!.Name;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "kyc detail: user projection lookup failed for {UserId}; name left null", view.UserId);
            }
        }

        return Ok(new KycSubmissionDetailResponse
        {
            Id = view.SubmissionId,
            UserId = view.UserId,
            UserName = userName,
            Status = view.Status,
            VehicleType = view.VehicleType,
            VehicleRegistration = view.VehicleRegistration,
            IdType = view.IdType,
            IdNumber = view.IdNumber,
            GrantsRole = view.GrantsRole,
            SubmittedAt = view.SubmittedAt,
            Images = new KycEvidenceImageUrls
            {
                IdFront = BuildImageUrl(view.SubmissionId, "id-front", view.IdFrontRef),
                IdBack = BuildImageUrl(view.SubmissionId, "id-back", view.IdBackRef),
                Selfie = BuildImageUrl(view.SubmissionId, "selfie", view.SelfieRef),
            },
        });
    }

    /// <summary>
    /// GET /admin/kyc/{id}/evidence/{slot}?token= — streams the evidence image
    /// bytes server-side/privileged through the gateway's CDN read proxy. The
    /// signed token IS the authorization (an &lt;img&gt; cannot carry a bearer);
    /// it is HMAC-bound to {id, slot} and ~300s lived, so a missing/expired/forged
    /// token is rejected. Mirrors DeliveriesController evidence + EarningsController
    /// signed-token patterns; the raw objectRef never leaves the gateway.
    /// </summary>
    [HttpGet("{id}/evidence/{slot}")]
    [AllowAnonymous]
    [PublicEndpoint("KYC admin evidence image — the HMAC token bound to {submissionId,slot} IS "
        + "the authorization; an <img> tag cannot send a bearer. The token is minted only by the "
        + "kyc.review-gated detail endpoint and the objectRef is resolved entirely server-side.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Evidence(
        string id, string slot, [FromQuery] string? token, CancellationToken ct)
    {
        if (!KycEvidenceTokenService.IsKnownSlot(slot)) return NotFound();

        // Unforgeable + short-lived + bound to exactly this {id, slot}. A missing,
        // expired, or tampered token never reaches the CDN.
        if (!_evidenceTokens.Validate(token, id, slot))
            return Unauthorized();

        KycBffSubmissionView? view;
        try
        {
            view = await _seam.GetByIdAsync(id, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }
        if (view is null) return NotFound();

        var reference = EvidenceSlots.First(s => s.Slot == slot).Ref(view);
        if (string.IsNullOrWhiteSpace(reference)) return NotFound();

        if (!_flags.CurrentValue.Cdn) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var cdn = _clients.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (cdn.BaseAddress is null) return StatusCode(StatusCodes.Status502BadGateway);
        if (!TryGetObjectReference(reference!, cdn.BaseAddress, out var objectRef)) return NotFound();

        var upstreamUri = new Uri(
            cdn.BaseAddress, CdnUploadUrlResolver.CdnFetchPathPrefix + Uri.EscapeDataString(objectRef));
        if (!CdnUploadUrlResolver.IsOnFetchPrefix(upstreamUri, cdn.BaseAddress)) return NotFound();

        HttpResponseMessage upstream;
        try
        {
            upstream = await cdn.GetAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogWarning(ex, "kyc evidence: cdn fetch failed for submission {Id} slot {Slot}", id, slot);
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        HttpContext.Response.RegisterForDispose(upstream);
        Response.Headers.CacheControl = "private, no-store";
        if (upstream.StatusCode == HttpStatusCode.NotFound) return NotFound();
        if (!upstream.IsSuccessStatusCode) return StatusCode(StatusCodes.Status502BadGateway);

        if (!AdminEvidenceResponsePolicy.HasSafeLength(upstream.Content.Headers.ContentLength))
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!AdminEvidenceResponsePolicy.TryApply(
                Response, upstream.Content.Headers.ContentType?.ToString(), out var contentType))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var declaredLength = upstream.Content.Headers.ContentLength!.Value;
        var stream = await upstream.Content.ReadAsStreamAsync(ct);
        return File(AdminEvidenceResponsePolicy.EnforceDeclaredLength(stream, declaredLength), contentType);
    }

    // Tokenized, self-authorizing gateway image URL for one slot; null when the
    // submission has no ref for that slot.
    private string? BuildImageUrl(string submissionId, string slot, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var (token, _) = _evidenceTokens.Create(submissionId, slot);
        return $"/admin/kyc/{Uri.EscapeDataString(submissionId)}/evidence/{slot}"
               + $"?token={Uri.EscapeDataString(token)}";
    }

    // Normalise a stored ref to a cdn fetch object path, fail-closed on SSRF /
    // traversal — same shape as AdminDeliveriesController.TryGetOwnedObjectReference.
    private static bool TryGetObjectReference(string reference, Uri cdnBaseAddress, out string objectReference)
    {
        objectReference = string.Empty;
        var candidate = reference.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            if ((absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                || !string.Equals(absolute.Host, cdnBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
                || absolute.Port != cdnBaseAddress.Port)
                return false;
            var marker = "/" + CdnUploadUrlResolver.CdnFetchPathPrefix;
            if (!absolute.AbsolutePath.StartsWith(marker, StringComparison.Ordinal)) return false;
            candidate = Uri.UnescapeDataString(absolute.AbsolutePath[marker.Length..]);
        }

        candidate = candidate.TrimStart('/');
        if (candidate.Length is not (> 0 and <= 512)
            || candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('%')
            || candidate.Contains('\\')
            || candidate.Contains('?')
            || candidate.Contains('#'))
            return false;
        objectReference = candidate;
        return true;
    }

    [HttpPatch("{id}/review")]
    [RequireCapability(Capabilities.KycReview)]
    [ProducesResponseType(typeof(KycReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Review(string id, [FromBody] KycReviewRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!KycAdminReviewComposer.TryParseAction(body.Action, out var action, out var actionError))
        {
            return BadRequest(new ProblemDetails
            {
                Title = actionError,
                Status = StatusCodes.Status400BadRequest
            });
        }

        var outcome = await _reviews.ReviewAsync(
            id, action, adminId, body.Reason, body.ResubmitSteps, HttpContext.TraceIdentifier, ct);

        if (outcome.Status != KycAdminReviewStatus.Ok)
        {
            return MapFailure(outcome);
        }

        return Ok(new KycReviewResponse
        {
            Submission = ToResponse(outcome.Result!),
            RoleGranted = outcome.RoleGranted,
            // Interim path delivers the status push inline; upstream path composes
            // notification async off the critical path (N14).
            PushSent = outcome.Result!.PushSent
        });
    }

    /// <summary>
    /// Shared failure translation for both review routes — same statuses, same RFC 7807
    /// shapes the native route has always emitted.
    /// </summary>
    internal IActionResult MapFailure(KycAdminReviewOutcome outcome) => outcome.Status switch
    {
        KycAdminReviewStatus.UpstreamDisabled => KycUpstreamDisabled(),
        KycAdminReviewStatus.NotFound => NotFound(),
        KycAdminReviewStatus.Conflict => StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
        {
            Title = outcome.Error,
            Status = StatusCodes.Status409Conflict
        }),
        KycAdminReviewStatus.InvalidRole => BadRequest(new ProblemDetails
        {
            // JEB-1472 / AC3: the {client,jeeber} whitelist rejects an unknown Jeeb contract
            // role at the gateway boundary; a non-contract role never reaches shared UM.
            Type = "https://jeeb.dev/errors/invalid-role",
            Title = "invalid_role",
            Detail = outcome.Error,
            Status = StatusCodes.Status400BadRequest
        }),
        _ => BadRequest(new ProblemDetails
        {
            Title = outcome.Error,
            Status = StatusCodes.Status400BadRequest
        })
    };

    internal IActionResult KycUpstreamDisabled() => StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProblemDetails
        {
            Type = "https://jeeb.dev/errors/upstream-unavailable",
            Title = "KYC upstream unavailable",
            Detail = "The KYC service is not enabled.",
            Status = StatusCodes.Status503ServiceUnavailable
        });

    private static KycQueueItem ToQueueItem(KycQueueSearchRow row) => new()
    {
        Id = row.Item.SubmissionId,
        UserId = row.Item.UserId,
        Status = row.Item.Status,
        SubmittedAt = row.Item.SubmittedAt,
        UserName = row.UserName,
        Phone = row.Phone,
        // vehicleType rides the kyc-service list row (cheap); vehicleRegistration is
        // only on the full submission, so the admin list still shows it via detail.
        VehicleType = row.Item.VehicleType ?? string.Empty,
        VehicleRegistration = string.Empty,
        LivenessPassed = false
    };

    private static KycSubmissionResponse ToResponse(KycBffReviewResult r) => new()
    {
        Id = r.SubmissionId,
        UserId = string.Empty,
        Status = r.Status,
        SubmittedAt = default,
        ReviewedAt = DateTimeOffset.UtcNow,
        RejectionReason = r.RejectionReason,
        VehicleType = string.Empty,
        VehicleRegistration = string.Empty,
        LivenessPassed = false,
        ResubmitSteps = r.ResubmitSteps.ToList()
    };
}
