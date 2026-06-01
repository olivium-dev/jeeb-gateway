using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin BFF surface over the real <c>remote-user-preferences</c> service
/// (host port 10067; tables <c>user_preferences</c>, <c>data_sets</c>), reached
/// through <see cref="IUserPreferencesClient"/>. The gateway holds NO
/// preferences state — every read/write resolves to the upstream's datastore.
///
/// Scoped to the authenticated caller: the userId is taken from the JWT subject
/// (falling back to the edge-injected <c>X-User-Id</c> header for the MVP) via
/// <see cref="UserIdentity"/>, so a caller can only read/write their own
/// preferences. The upstream's <c>{user_id}</c> path segment is therefore never
/// attacker-controlled at this layer.
///
/// Gated by <c>FeatureFlags:UseUpstream:RemoteUserPreferences</c>. Because this
/// path is net-new (there is no legacy in-memory store to fall back to), the
/// flag is a runtime kill switch: when off, the endpoints return 503
/// ProblemDetails rather than calling an unconfigured/disabled downstream.
/// </summary>
[ApiController]
[Route("users/me/preferences")]
public class UserPreferencesController : ControllerBase
{
    private const int MaxKeyLength = 256;
    private const int MaxValueLength = 8192;

    private readonly IUserPreferencesClient _preferences;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public UserPreferencesController(
        IUserPreferencesClient preferences,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _preferences = preferences;
        _flags = flags;
    }

    /// <summary>
    /// Returns every preference key/value for the authenticated user.
    /// Real path: <c>GET /preferences/{user_id}</c> on remote-user-preferences.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyDictionary<string, string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAll(CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (!_flags.CurrentValue.RemoteUserPreferences) return UpstreamDisabled();

        var prefs = await _preferences.GetAllAsync(userId, ct);
        return Ok(prefs);
    }

    /// <summary>
    /// Returns a single preference value for the authenticated user, or 404 when
    /// the key has never been set. Real path:
    /// <c>GET /preferences/{user_id}/{pref_key}</c>.
    /// </summary>
    [HttpGet("{prefKey}")]
    [ProducesResponseType(typeof(PreferenceValueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(string prefKey, CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (string.IsNullOrWhiteSpace(prefKey) || prefKey.Length > MaxKeyLength) return InvalidKey();
        if (!_flags.CurrentValue.RemoteUserPreferences) return UpstreamDisabled();

        var value = await _preferences.GetAsync(userId, prefKey, ct);
        if (value is null)
        {
            return Problem(
                title: "Preference not found",
                detail: $"Preference '{prefKey}' is not set for the current user.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(new PreferenceValueResponse { Value = value });
    }

    /// <summary>
    /// Creates or updates a single preference value for the authenticated user.
    /// Real path: <c>POST /preferences/{user_id}/{pref_key}</c>.
    /// </summary>
    [HttpPut("{prefKey}")]
    [ProducesResponseType(typeof(PreferenceValueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Set(
        string prefKey,
        [FromBody] PreferenceValueResponse body,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;
        if (string.IsNullOrWhiteSpace(prefKey) || prefKey.Length > MaxKeyLength) return InvalidKey();
        if (body is null || body.Value is null)
        {
            return Problem(
                title: "Invalid preference value",
                detail: "Request body must be a JSON object with a non-null 'value' field.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (body.Value.Length > MaxValueLength)
        {
            return Problem(
                title: "Preference value too large",
                detail: $"Preference value must be at most {MaxValueLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!_flags.CurrentValue.RemoteUserPreferences) return UpstreamDisabled();

        await _preferences.SetAsync(userId, prefKey, body.Value, ct);
        return Ok(new PreferenceValueResponse { Value = body.Value });
    }

    private IActionResult UpstreamDisabled() => Problem(
        title: "User-preferences upstream disabled",
        detail: "FeatureFlags:UseUpstream:RemoteUserPreferences is off in this environment.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private IActionResult InvalidKey() => Problem(
        title: "Invalid preference key",
        detail: $"Preference key must be non-empty and at most {MaxKeyLength} characters.",
        statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>
/// BFF response envelope for a single preference value. Mirrors the upstream
/// <c>PreferenceValue</c> schema (<c>{ "value": "..." }</c>) so the mobile client
/// sees a stable shape regardless of how the upstream evolves.
/// </summary>
public sealed class PreferenceValueResponse
{
    public string Value { get; init; } = string.Empty;
}
