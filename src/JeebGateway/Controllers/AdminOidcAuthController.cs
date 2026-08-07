using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Auth.Oidc;
using JeebGateway.Security;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Controllers;

/// <summary>
/// Stateless external administrator sign-in. Authorization state, nonce, PKCE
/// verifier, and local return path live only in an authenticated HttpOnly cookie;
/// no gateway database or process session is introduced.
/// </summary>
[ApiController]
[Route("admin/v1/auth/oidc")]
[AllowAnonymous]
[PublicEndpoint("External administrator OIDC authorization-code callback authenticates before a bearer exists.")]
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
public sealed class AdminOidcAuthController : ControllerBase
{
    private const int MaximumProviderResponseBytes = 1_000_000;
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _clients;
    private readonly ITokenService _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<AdminOidcAuthController> _logger;

    public AdminOidcAuthController(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IHttpClientFactory clients,
        ITokenService tokens,
        TimeProvider clock,
        ILogger<AdminOidcAuthController> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _clients = clients;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Start([FromQuery] string? returnPath = null)
    {
        PreventCaching();
        if (!IsSameOriginNavigation())
            return ProblemResult(403, "origin_rejected", "The sign-in request was rejected.");
        if (!TryOptions(out var options, out var unavailable)) return unavailable;
        options.TryStateKey(out var stateKey);

        var correlation = new AdminOidcCorrelation(
            AdminOidcCorrelationProtector.RandomValue(),
            AdminOidcCorrelationProtector.RandomValue(),
            AdminOidcCorrelationProtector.RandomValue(48),
            SafeReturnPath(returnPath),
            _clock.GetUtcNow().ToUnixTimeSeconds(),
            Request.Cookies.TryGetValue(AdminSessionCookies.RefreshCookie, out var existingRefresh)
                && !string.IsNullOrWhiteSpace(existingRefresh)
                && existingRefresh.Length <= 512
                ? existingRefresh
                : null);
        var lifetime = TimeSpan.FromMinutes(options.CorrelationLifetimeMinutes);
        AdminSessionCookies.SetOidcCorrelation(
            Response, AdminOidcCorrelationProtector.Protect(correlation, stateKey), lifetime);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = options.RedirectUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', options.Scopes.Distinct(StringComparer.Ordinal)),
            ["state"] = correlation.State,
            ["nonce"] = correlation.Nonce,
            ["code_challenge"] = AdminOidcCorrelationProtector.CodeChallenge(correlation.CodeVerifier),
            ["code_challenge_method"] = "S256",
            ["max_age"] = "0",
            ["prompt"] = "login",
        };
        if (!string.IsNullOrWhiteSpace(options.RequiredAcrValues))
            query["acr_values"] = options.RequiredAcrValues;
        return Redirect(QueryHelpers.AddQueryString(options.AuthorizationEndpoint, query));
    }

    [HttpGet("callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        PreventCaching();
        if (!TryOptions(out var options, out var unavailable)) return unavailable;
        options.TryStateKey(out var stateKey);

        Request.Cookies.TryGetValue(AdminSessionCookies.OidcCorrelationCookie, out var protectedCorrelation);
        AdminSessionCookies.DeleteOidcCorrelation(Response);
        if (string.IsNullOrWhiteSpace(protectedCorrelation)
            || !AdminOidcCorrelationProtector.TryUnprotect(protectedCorrelation, stateKey, out var correlation)
            || correlation is null)
            return AuthenticationRejected("missing_correlation");

        var now = _clock.GetUtcNow();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(correlation.IssuedAt);
        if (issuedAt > now.AddSeconds(30)
            || now - issuedAt > TimeSpan.FromMinutes(options.CorrelationLifetimeMinutes)
            || string.IsNullOrWhiteSpace(state)
            || state.Length > 512
            || !AdminOidcCorrelationProtector.FixedEquals(correlation.State, state))
            return AuthenticationRejected("invalid_state");
        if (!string.IsNullOrWhiteSpace(error)) return AuthenticationRejected("provider_rejected");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 8_192)
            return AuthenticationRejected("missing_code");

        try
        {
            var tokenJson = await ExchangeCodeAsync(options, code, correlation.CodeVerifier, ct);
            using var tokenDocument = JsonDocument.Parse(tokenJson);
            if (!tokenDocument.RootElement.TryGetProperty("id_token", out var idTokenElement)
                || idTokenElement.ValueKind != JsonValueKind.String)
                return AuthenticationRejected("missing_id_token");

            var jwksJson = await GetBoundedAsync(options.JwksUri, ct);
            var identity = AdminOidcTokenValidator.Validate(
                idTokenElement.GetString()!, jwksJson, correlation.Nonce, options, now);
            if (!string.IsNullOrWhiteSpace(correlation.PreviousRefreshToken))
                await _tokens.RevokeAsync(
                    correlation.PreviousRefreshToken, RevocationReason.Reauthenticated, ct);
            var pair = await _tokens.IssueAsync(
                identity.UserId,
                identity.Roles,
                identity.Roles[0],
                identity.Authentication,
                ct);
            AdminSessionCookies.Set(Request, Response, pair.RefreshToken);
            _logger.LogInformation(
                "admin.oidc authenticated operatorId={OperatorId} roles={Roles}",
                identity.UserId,
                string.Join(',', identity.Roles));
            return LocalRedirect(correlation.ReturnPath);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                                 or TaskCanceledException
                                                 or JsonException
                                                 or SecurityTokenException
                                                 or ArgumentException
                                                 or InvalidOperationException)
        {
            _logger.LogWarning(exception, "admin.oidc callback rejected");
            return AuthenticationRejected("identity_verification_failed");
        }
    }

    private async Task<string> ExchangeCodeAsync(
        AdminOidcOptions options,
        string code,
        string verifier,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri,
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["code_verifier"] = verifier,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var response = await _clients.CreateClient("AdminOidc").SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new SecurityTokenException("The OIDC token endpoint rejected the authorization code.");
        return await ReadBoundedAsync(response.Content, timeout.Token);
    }

    private async Task<string> GetBoundedAsync(string uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);
        using var response = await _clients.CreateClient("AdminOidc").SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The OIDC signing keys are unavailable.");
        return await ReadBoundedAsync(response.Content, timeout.Token);
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumProviderResponseBytes)
            throw new InvalidOperationException("The OIDC response is oversized.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > MaximumProviderResponseBytes)
                throw new InvalidOperationException("The OIDC response is oversized.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private bool TryOptions(out AdminOidcOptions options, out IActionResult unavailable)
    {
        if (AdminOidcOptions.TryLoad(_configuration, out options, out var error))
        {
            unavailable = null!;
            return true;
        }
        _logger.LogError("admin.oidc unavailable: {ConfigurationError}", error);
        unavailable = ProblemResult(
            503, "admin_identity_unavailable", "External administrator identity is not configured.");
        return false;
    }

    private bool IsSameOriginNavigation()
    {
        if (!Request.Headers.TryGetValue("Sec-Fetch-Site", out var site))
            return _environment.IsDevelopment() || _environment.IsEnvironment("Testing");
        return string.Equals(site.ToString(), "same-origin", StringComparison.OrdinalIgnoreCase)
               || string.Equals(site.ToString(), "none", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeReturnPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value[0] != '/'
            || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\')
            || value.Any(char.IsControl) || value.Contains('?') || value.Contains('#')) return "/";
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "/";
        if (segments[0] is not ("cases" or "deliveries" or "settlements")) return "/";
        return segments.Skip(1).All(static segment => segment.Length is > 0 and <= 128
            && segment.All(static character => char.IsAsciiLetterOrDigit(character)
                                                   || character is '.' or '_' or '~' or '-'))
            ? value
            : "/";
    }

    private ObjectResult AuthenticationRejected(string reason)
    {
        _logger.LogWarning("admin.oidc authentication rejected reason={Reason}", reason);
        return ProblemResult(401, "admin_authentication_rejected", "Administrator authentication was rejected.");
    }

    private void PreventCaching()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private ObjectResult ProblemResult(int status, string type, string detail) => new(new ProblemDetails
    {
        Status = status,
        Type = $"https://jeeb.dev/errors/{type}",
        Title = type,
        Detail = detail,
        Instance = Request.Path,
    })
    {
        StatusCode = status,
        ContentTypes = { "application/problem+json" },
    };
}
