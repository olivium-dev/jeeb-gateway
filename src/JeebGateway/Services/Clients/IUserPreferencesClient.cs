namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the real <c>remote-user-preferences</c> service (the
/// fleet-wide user-preference store, internal port 10023; published on the jeeb
/// swarm at host port 10067). Hand-coded against the verified routes in the
/// committed contract <c>contracts/remote-user-preferences.openapi.json</c>
/// (OpenAPI 3, served live at <c>/api-docs/openapi.json</c>):
///   GET  /preferences/{user_id}              -> Data_GetPreferences
///   GET  /preferences/{user_id}/{pref_key}   -> Data_GetSinglePreference
///   POST /preferences/{user_id}/{pref_key}   -> Data_SetSinglePreference
///
/// Storage is the service's own datastore (tables <c>user_preferences</c>,
/// <c>data_sets</c>); the gateway owns NO preferences state — this is the
/// thin-BFF seam that lets <see cref="JeebGateway.Controllers.UserPreferencesController"/>
/// validate -> call -> map with zero local record-of-truth.
///
/// The named "remote-user-preferences" HttpClient registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/> supplies
/// BaseAddress (<c>Services:RemoteUserPreferences:BaseUrl</c>) + the org-standard
/// resilience pipeline (retry / circuit-breaker / timeout), so this class never
/// thinks about retry/timeout/breaker.
///
/// The upstream returns snake_case JSON (the <c>Preferences</c> map and the
/// <c>{ "value": "..." }</c> envelope), handled via
/// <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/> plus explicit
/// <c>[JsonPropertyName]</c> on the wire DTOs.
///
/// All methods throw <see cref="HttpRequestException"/> on a non-2xx that is not
/// an expected 404 (single-preference reads surface 404 as a <c>null</c> value).
/// </summary>
public interface IUserPreferencesClient
{
    /// <summary>
    /// Fetches every preference key/value for the user.
    /// Proxies <c>GET /preferences/{user_id}</c>.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Fetches a single preference value, or <c>null</c> when the upstream
    /// returns 404 (key not set). Proxies <c>GET /preferences/{user_id}/{pref_key}</c>.
    /// </summary>
    Task<string?> GetAsync(string userId, string prefKey, CancellationToken ct);

    /// <summary>
    /// Creates or updates a single preference value.
    /// Proxies <c>POST /preferences/{user_id}/{pref_key}</c>.
    /// </summary>
    Task SetAsync(string userId, string prefKey, string value, CancellationToken ct);
}
