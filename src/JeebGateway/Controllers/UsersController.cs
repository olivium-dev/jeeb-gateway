using System.Text.RegularExpressions;
using JeebGateway.Requests;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("users/me")]
public class UsersController : ControllerBase
{
    private static readonly Regex LanguageRegex = new("^[a-z]{2}(-[A-Z]{2})?$", RegexOptions.Compiled);
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"^\+?[0-9]{7,15}$", RegexOptions.Compiled);

    private readonly IUsersStore _store;
    private readonly ITokenService _tokens;
    private readonly IAccountDeletionStore _deletions;
    private readonly IRequestsStore _requests;

    public UsersController(
        IUsersStore store,
        ITokenService tokens,
        IAccountDeletionStore deletions,
        IRequestsStore requests)
    {
        _store = store;
        _tokens = tokens;
        _deletions = deletions;
        _requests = requests;
    }

    [HttpGet]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var profile = await _store.GetOrCreateAsync(userId, ct);
        var addresses = await _store.ListAddressesAsync(userId, ct);
        return Ok(ToResponse(profile, addresses));
    }

    [HttpPatch]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PatchMe([FromBody] UpdateProfileRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Name is not null && string.IsNullOrWhiteSpace(body.Name))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Name cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Language is { } lang && !LanguageRegex.IsMatch(lang))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Language must be a BCP-47 tag like 'en' or 'en-US'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!string.IsNullOrEmpty(body.Email) && !EmailRegex.IsMatch(body.Email))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Email is not a valid address.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var patch = new ProfilePatch
        {
            Name = body.Name,
            AvatarUrl = body.AvatarUrl,
            Language = body.Language,
            Email = body.Email
        };
        var updated = await _store.UpdateProfileAsync(userId, patch, ct);
        var addresses = await _store.ListAddressesAsync(userId, ct);
        return Ok(ToResponse(updated, addresses));
    }

    // -----------------------------------------------------------------
    // Account deletion (T-backend-035, GDPR-like)
    //
    // The DELETE response is intentionally 202 Accepted, not 204: the
    // user's data is queued for deletion, not removed in-band. Callers
    // get the deletion record back so the mobile app can show
    // "scheduled for <date>" vs. "waiting for active delivery".
    //
    // Active deliveries gate the 30-day clock — the deletion store does
    // not anonymize order rows or start the timer until every request
    // in RequestStatus.ActiveStates is terminal.
    // -----------------------------------------------------------------

    [HttpDelete]
    [ProducesResponseType(typeof(AccountDeletionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var activeCount = await _requests.CountActiveForClientAsync(userId, ct);
        var record = await _deletions.RequestAsync(userId, activeCount > 0, ct);

        return StatusCode(StatusCodes.Status202Accepted, ToResponse(record));
    }

    [HttpGet("deletion")]
    [ProducesResponseType(typeof(AccountDeletionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeletion(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        var record = await _deletions.GetAsync(userId, ct);
        if (record is null) return NotFound();
        return Ok(ToResponse(record));
    }

    private static AccountDeletionResponse ToResponse(AccountDeletionRequest r) => new()
    {
        UserId = r.UserId,
        Status = r.Status,
        RequestedAt = r.RequestedAt,
        ScheduledPurgeAt = r.ScheduledPurgeAt,
        CompletedAt = r.CompletedAt
    };

    // -----------------------------------------------------------------
    // Credential change endpoints (T-backend-043)
    // Both flows revoke every outstanding refresh token for the user so
    // any other device session is forced to re-authenticate.
    // -----------------------------------------------------------------

    [HttpPost("password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null
            || string.IsNullOrWhiteSpace(body.CurrentPassword)
            || string.IsNullOrWhiteSpace(body.NewPassword))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "currentPassword and newPassword are required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.NewPassword.Length < 8)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "newPassword must be at least 8 characters.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Password persistence lives in auth-service. The gateway's
        // contract here is "credentials changed → revoke all sessions".
        await _tokens.RevokeAllForUserAsync(userId, RevocationReason.PasswordChanged, ct);
        return NoContent();
    }

    [HttpPost("phone")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePhone([FromBody] ChangePhoneRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null
            || string.IsNullOrWhiteSpace(body.NewPhone)
            || string.IsNullOrWhiteSpace(body.OtpCode))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "newPhone and otpCode are required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!PhoneRegex.IsMatch(body.NewPhone))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "newPhone must match ^\\+?[0-9]{7,15}$.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        await _tokens.RevokeAllForUserAsync(userId, RevocationReason.PhoneChanged, ct);
        return NoContent();
    }

    // -----------------------------------------------------------------
    // Saved addresses CRUD
    // -----------------------------------------------------------------

    [HttpGet("addresses")]
    [ProducesResponseType(typeof(IReadOnlyList<SavedAddressResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAddresses(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;
        var addresses = await _store.ListAddressesAsync(userId, ct);
        return Ok(addresses.Select(ToResponse).ToList());
    }

    [HttpPost("addresses")]
    [ProducesResponseType(typeof(SavedAddressResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateAddress([FromBody] SavedAddressUpsertRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null
            || string.IsNullOrWhiteSpace(body.Label)
            || string.IsNullOrWhiteSpace(body.Line1))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "label and line1 are required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!CountryIsValid(body.Country)
            || !LatLngPaired(body.Latitude, body.Longitude))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "country must be ISO-3166-1 alpha-2; latitude/longitude must be provided together.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var addr = await _store.CreateAddressAsync(userId, ToUpsert(body), ct);
            return CreatedAtAction(nameof(GetAddress), new { id = addr.Id }, ToResponse(addr));
        }
        catch (DuplicateAddressLabelException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    [HttpGet("addresses/{id}")]
    [ProducesResponseType(typeof(SavedAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAddress(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;
        var addr = await _store.GetAddressAsync(userId, id, ct);
        if (addr is null) return NotFound();
        return Ok(ToResponse(addr));
    }

    [HttpPatch("addresses/{id}")]
    [ProducesResponseType(typeof(SavedAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAddress(string id, [FromBody] SavedAddressUpsertRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Label is not null && string.IsNullOrWhiteSpace(body.Label))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "label cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Line1 is not null && string.IsNullOrWhiteSpace(body.Line1))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "line1 cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!CountryIsValid(body.Country)
            || !LatLngPaired(body.Latitude, body.Longitude))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "country must be ISO-3166-1 alpha-2; latitude/longitude must be provided together.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var updated = await _store.UpdateAddressAsync(userId, id, ToUpsert(body), ct);
            if (updated is null) return NotFound();
            return Ok(ToResponse(updated));
        }
        catch (DuplicateAddressLabelException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    [HttpDelete("addresses/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAddress(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem)) return problem;
        var removed = await _store.DeleteAddressAsync(userId, id, ct);
        return removed ? NoContent() : NotFound();
    }

    // -----------------------------------------------------------------
    // Mapping helpers
    // -----------------------------------------------------------------

    private static bool CountryIsValid(string? country)
    {
        if (string.IsNullOrEmpty(country)) return true;
        return country.Length == 2 && country.All(c => c is >= 'A' and <= 'Z');
    }

    private static bool LatLngPaired(decimal? lat, decimal? lng)
    {
        if (lat.HasValue != lng.HasValue) return false;
        if (lat.HasValue && (lat < -90 || lat > 90)) return false;
        if (lng.HasValue && (lng < -180 || lng > 180)) return false;
        return true;
    }

    private static AddressUpsert ToUpsert(SavedAddressUpsertRequest body) => new()
    {
        Label = body.Label,
        Line1 = body.Line1,
        Line2 = body.Line2,
        City = body.City,
        Country = body.Country,
        Latitude = body.Latitude,
        Longitude = body.Longitude,
        IsDefault = body.IsDefault
    };

    private static UserProfileResponse ToResponse(UserProfile p, IReadOnlyList<SavedAddress> addresses) => new()
    {
        Id = p.Id,
        Phone = p.Phone,
        Email = p.Email,
        Name = p.Name,
        AvatarUrl = p.AvatarUrl,
        Language = p.Language,
        Roles = p.Roles,
        Rating = p.Rating,
        RatingCount = p.RatingCount,
        SavedAddresses = addresses.Select(ToResponse).ToList(),
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        IsSuspended = p.IsSuspended,
        SuspensionReason = p.SuspensionReason,
        SuspendedAt = p.SuspendedAt,
        IsNew = p.RatingCount == 0,
        ActiveRole = p.ActiveRole,
        RoleSwitchedAt = p.RoleSwitchedAt
    };

    private static SavedAddressResponse ToResponse(SavedAddress a) => new()
    {
        Id = a.Id,
        Label = a.Label,
        Line1 = a.Line1,
        Line2 = a.Line2,
        City = a.City,
        Country = a.Country,
        Latitude = a.Latitude,
        Longitude = a.Longitude,
        IsDefault = a.IsDefault,
        CreatedAt = a.CreatedAt,
        UpdatedAt = a.UpdatedAt
    };
}
