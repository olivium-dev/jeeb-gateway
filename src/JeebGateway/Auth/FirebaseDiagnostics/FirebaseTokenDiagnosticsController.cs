using JeebGateway.Auth.Capabilities;
using JeebGateway.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.FirebaseDiagnostics;

[ApiController]
[Route("v1/auth")]
[Route("auth")]
[AllowAnonymous]
[PublicEndpoint("Non-production Firebase token-verification diagnostic; never mints a session.")]
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
public sealed class FirebaseTokenDiagnosticsController(
    IUserManagementFirebaseTokenDiagnosticClient userManagement,
    IOptions<FirebaseTokenDiagnosticsOptions> options,
    IHostEnvironment environment) : ControllerBase
{
    private const int MaxIdTokenLength = 16 * 1024;
    private const int MaxSubjectLength = 128;
    private const long MaximumRequestBodyBytes = 20 * 1024;

    [HttpPost("diagnostics/firebase-token")]
    [RequestSizeLimit(MaximumRequestBodyBytes)]
    [ProducesResponseType(typeof(FirebaseTokenDiagnosticResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Verify(
        [FromBody] FirebaseTokenDiagnosticRequest? body,
        CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!FirebaseTokenDiagnosticsOptions.IsAllowed(environment, configured))
        {
            return NotFound();
        }

        if (body is null
            || string.IsNullOrWhiteSpace(body.IdToken)
            || body.IdToken.Length > MaxIdTokenLength
            || string.IsNullOrWhiteSpace(body.ExpectedProjectId)
            || string.IsNullOrWhiteSpace(body.ExpectedSubject)
            || body.ExpectedSubject.Length > MaxSubjectLength
            || !string.Equals(body.ExpectedProjectId, configured.ProjectId, StringComparison.Ordinal))
        {
            return BadRequest(new { verified = false });
        }

        FirebaseTokenDiagnosticResult result;
        try
        {
            result = await userManagement.VerifyAsync(
                body.IdToken,
                configured.ProjectId,
                body.ExpectedSubject,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { verified = false });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { verified = false });
        }

        return result.Outcome switch
        {
            FirebaseTokenDiagnosticOutcome.Verified when result.Response is not null
                => Ok(result.Response),
            FirebaseTokenDiagnosticOutcome.Invalid
                => Unauthorized(new { verified = false }),
            FirebaseTokenDiagnosticOutcome.Disabled
                => NotFound(),
            FirebaseTokenDiagnosticOutcome.Unavailable
                => StatusCode(StatusCodes.Status503ServiceUnavailable, new { verified = false }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { verified = false }),
        };
    }
}
