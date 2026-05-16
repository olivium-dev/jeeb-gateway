using System.Security.Claims;
using JeebGateway.NotificationPreferences;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("users/me/notification-preferences")]
public class NotificationPreferencesController : ControllerBase
{
    private readonly INotificationPreferencesStore _store;

    public NotificationPreferencesController(INotificationPreferencesStore store)
    {
        _store = store;
    }

    [HttpGet]
    [ProducesResponseType(typeof(NotificationPreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;

        var prefs = await _store.GetAsync(userId, ct);
        return Ok(ToResponse(prefs));
    }

    [HttpPatch]
    [ProducesResponseType(typeof(NotificationPreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Patch([FromBody] NotificationPreferencesPatchRequest body, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (body.Otp is false || body.SystemCritical is false)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "OTP and system_critical channels cannot be disabled.",
                Detail = "Remove 'otp' and 'systemCritical' from the request body, or set them to true.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var patch = new NotificationPreferencesPatch
        {
            Offers = body.Offers,
            Chat = body.Chat,
            StatusChanges = body.StatusChanges,
            RatingReminders = body.RatingReminders
        };

        var updated = await _store.UpdateAsync(userId, patch, ct);
        return Ok(ToResponse(updated));
    }

    private bool TryGetUserId(out string userId, out IActionResult problem)
    {
        var fromClaim = User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User?.FindFirstValue("sub");

        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            userId = fromClaim;
            problem = null!;
            return true;
        }

        // MVP fallback while JWT validation isn't wired up yet: header injected by edge.
        if (Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            userId = header.ToString();
            problem = null!;
            return true;
        }

        userId = string.Empty;
        problem = Unauthorized();
        return false;
    }

    private static NotificationPreferencesResponse ToResponse(UserNotificationPreferences prefs) => new()
    {
        UserId = prefs.UserId,
        Preferences = new NotificationPreferencesCategoryToggles
        {
            Offers = prefs.Offers,
            Chat = prefs.Chat,
            StatusChanges = prefs.StatusChanges,
            RatingReminders = prefs.RatingReminders
        },
        AlwaysOn = NotificationPreferencesDefaults.AlwaysOnChannels,
        UpdatedAt = prefs.UpdatedAt
    };
}
