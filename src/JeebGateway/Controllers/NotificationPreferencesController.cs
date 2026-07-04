using JeebGateway.Auth.Capabilities;
using JeebGateway.NotificationPreferences;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
// Canonical surface (kept for back-compat with existing callers)...
[Route("api/users/me/notification-preferences")]
// ...plus the path the mobile app actually calls (JM notif-prefs). The mobile
// DioNotificationsRepository dials GET/PATCH /v1/notifications/preferences; the
// gateway previously only exposed the api/users/me/... shape, so the mobile call
// 404'd. Both routes map to the SAME actions (GET + PATCH) — a pure alias, no
// behaviour change, ADR-0001 (stateless thin BFF) preserved.
[Route("v1/notifications/preferences")]
[RequireCapability(Capabilities.NotificationPrefsSelf)]
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
            return BadRequest(new ProblemDetails { Title = "Request body is required.", Status = StatusCodes.Status400BadRequest });

        if (body.Otp is false || body.SystemCritical is false || body.Kyc is false || body.Disputes is false)
            return BadRequest(new ProblemDetails
            {
                Title = "Transactional channels cannot be disabled.",
                Detail = "Remove 'otp', 'systemCritical', 'kyc', and 'disputes' from the request body, or set them to true.",
                Status = StatusCodes.Status400BadRequest
            });

        var patch = new NotificationPreferencesPatch
        {
            Offers = body.Offers,
            Chat = body.Chat,
            StatusChanges = body.StatusChanges,
            RatingReminders = body.RatingReminders,
            Promotions = body.Promotions,
            Settlements = body.Settlements
        };

        var updated = await _store.UpdateAsync(userId, patch, ct);
        return Ok(ToResponse(updated));
    }

    // SEC-C1 (Leg-11): identity derives from the validated JWT principal; the raw
    // X-User-Id header is honoured ONLY when EdgeIdentityTrust permits it (Dev/Testing or
    // a secret-gated trusted edge). Delegating to the shared, gated UserIdentity closes the
    // spoof/IDOR — a raw client can no longer read/write another user's notification prefs.
    private bool TryGetUserId(out string userId, out IActionResult problem)
        => UserIdentity.TryGetUserId(HttpContext, out userId, out problem);

    private static NotificationPreferencesResponse ToResponse(UserNotificationPreferences prefs) => new()
    {
        UserId = prefs.UserId,
        Preferences = new NotificationPreferencesCategoryToggles
        {
            Offers = prefs.Offers,
            Chat = prefs.Chat,
            StatusChanges = prefs.StatusChanges,
            RatingReminders = prefs.RatingReminders,
            Promotions = prefs.Promotions,
            Settlements = prefs.Settlements
        },
        AlwaysOn = NotificationPreferencesDefaults.AlwaysOnChannels,
        UpdatedAt = prefs.UpdatedAt
    };
}
