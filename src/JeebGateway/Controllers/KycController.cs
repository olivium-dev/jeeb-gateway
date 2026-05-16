using JeebGateway.Kyc;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-004 / JEEB-22: Jeeber KYC submission and status lookup.
///
/// POST /kyc/submit accepts multipart upload (ID front, ID back, selfie,
/// vehicle type, vehicle registration), stores each document in
/// encrypted object storage, runs the liveness check stub, and creates a
/// queue entry with status <c>pending_review</c> for the admin moderation
/// pipeline.
///
/// GET /kyc/status returns the most recent submission so the mobile app
/// can switch between "under review", "verified", and "rejected".
/// </summary>
[ApiController]
[Route("kyc")]
public class KycController : ControllerBase
{
    private static readonly IReadOnlySet<string> AllowedImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/heic"
    };

    private readonly IKycService _service;

    public KycController(IKycService service)
    {
        _service = service;
    }

    [HttpPost("submit")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(KycSubmissionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Submit(
        [FromForm] IFormFile? idFront,
        [FromForm] IFormFile? idBack,
        [FromForm] IFormFile? selfie,
        [FromForm] string? vehicleType,
        [FromForm] string? vehicleRegistration,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "vehicleType is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(vehicleRegistration))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "vehicleRegistration is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!TryReadDocument(idFront, "idFront", out var idFrontInput, out var idFrontError))
        {
            return BadRequest(idFrontError);
        }
        if (!TryReadDocument(idBack, "idBack", out var idBackInput, out var idBackError))
        {
            return BadRequest(idBackError);
        }
        if (!TryReadDocument(selfie, "selfie", out var selfieInput, out var selfieError))
        {
            return BadRequest(selfieError);
        }

        var input = new KycSubmissionInput
        {
            UserId = userId,
            VehicleType = vehicleType!.Trim(),
            VehicleRegistration = vehicleRegistration!.Trim(),
            IdFront = await ToInput(idFrontInput!, ct),
            IdBack = await ToInput(idBackInput!, ct),
            Selfie = await ToInput(selfieInput!, ct)
        };

        var submission = await _service.SubmitAsync(input, ct);
        return StatusCode(StatusCodes.Status202Accepted, ToResponse(submission));
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(KycStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var latest = await _service.GetLatestStatusAsync(userId, ct);
        return Ok(new KycStatusResponse
        {
            UserId = userId,
            HasSubmission = latest is not null,
            Latest = latest is null ? null : ToResponse(latest)
        });
    }

    private static bool TryReadDocument(IFormFile? file, string fieldName, out IFormFile parsed, out ProblemDetails error)
    {
        parsed = file!;
        error = null!;
        if (file is null || file.Length == 0)
        {
            error = new ProblemDetails
            {
                Title = $"{fieldName} is required and must not be empty.",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        if (!AllowedImageTypes.Contains(file.ContentType))
        {
            error = new ProblemDetails
            {
                Title = $"{fieldName} must be one of: {string.Join(", ", AllowedImageTypes)}.",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        return true;
    }

    private static async Task<KycDocumentInput> ToInput(IFormFile file, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, ct);
        return new KycDocumentInput
        {
            FileName = file.FileName,
            ContentType = file.ContentType,
            Bytes = stream.ToArray()
        };
    }

    private static KycSubmissionResponse ToResponse(KycSubmission s) => new()
    {
        Id = s.Id,
        UserId = s.UserId,
        Status = s.Status,
        SubmittedAt = s.SubmittedAt,
        ReviewedAt = s.ReviewedAt,
        RejectionReason = s.RejectionReason,
        VehicleType = s.VehicleType,
        VehicleRegistration = s.VehicleRegistration,
        LivenessPassed = s.LivenessPassed
    };
}
