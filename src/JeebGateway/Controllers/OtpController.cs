using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin BFF surface over the shared <c>one-time-password</c> service (OTPApi;
/// live swarm overlay, host port 10037 — see <c>ServiceOTPApi:BaseUrl</c> and
/// <c>Services:ServiceOTP:BaseUrl</c> in <c>appsettings.Production.json</c>).
/// This is a generic OTP send/validate proxy that consumes the SAME
/// NSwag-generated <see cref="IServiceOTPClient"/> already used by the
/// delivery-handover path (<see cref="DeliveriesController"/>), wired with the
/// org-standard bearer + X-Service-Auth + Polly resilience pipeline in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions.AddDownstreamClients"/>.
///
/// It is ADDITIVE and route-distinct from the two existing OTP surfaces:
///   - phone sign-in lives at <c>POST /v1/auth/otp/{request,verify}</c>
///     (<see cref="JeebGateway.Auth.OtpSignIn.AuthOtpController"/>);
///   - the 4-digit delivery handover lives under
///     <c>POST /api/deliveries/{id}/otp/...</c> (<see cref="DeliveriesController"/>).
/// This controller exposes the raw generic OTP primitives under
/// <c>/api/otp</c> for callers that need a direct send/validate against the
/// shared service (e.g. application-scoped OTP outside the two specialised
/// flows).
///
/// Gated by <c>FeatureFlags:UseUpstream:Otp</c>: when off the controller returns
/// a 503 ProblemDetails instead of calling localhost — there is no in-memory
/// fallback for a one-time-password service, so the kill switch fails closed.
///
/// Serves JEB-1471, JEB-1467, JEB-1459, JEB-1455, JEB-1441, JEB-1437, JEB-1433,
/// JEB-1430, JEB-626, JEB-625, JEB-471, JEB-158, JEB-159, JEB-55, JEB-49,
/// JEB-37, JEB-38, JEB-39 (OTP send/validate for phone sign-in and the 4-digit
/// delivery_handover OTP).
/// </summary>
[ApiController]
[Route("api/otp")]
[Produces("application/json", "application/problem+json")]
public sealed class OtpController : ControllerBase
{
    private readonly IServiceOTPClient _otpClient;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<OtpController> _log;

    public OtpController(
        IServiceOTPClient otpClient,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<OtpController> log)
    {
        _otpClient = otpClient;
        _flags = flags;
        _log = log;
    }

    /// <summary>
    /// POST /api/otp/send — request a one-time-password for a phone number
    /// scoped to an application id. The 6-digit code is generated and stored by
    /// the OTP service and delivered out-of-band (Twilio SMS); it is never on
    /// the wire. Returns 202 Accepted on a successful trigger.
    /// </summary>
    [HttpPost("send")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Send([FromBody] OtpSendRequest request, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Otp)
            return UpstreamDisabled();

        if (request is null
            || string.IsNullOrWhiteSpace(request.PhoneNumber)
            || string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Problem(
                title: "Invalid OTP send request",
                detail: "phoneNumber and applicationId are required.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        try
        {
            await _otpClient.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber = request.PhoneNumber!,
                ApplicationId = request.ApplicationId!
            }, ct);

            // Never log the phone or any OTP-adjacent data — only the
            // application partition (PR review B5 precedent in DeliveriesController).
            _log.LogInformation(
                "OTP send triggered for applicationId {ApplicationId}", request.ApplicationId);

            return Accepted();
        }
        catch (ApiException ex)
        {
            return UpstreamFault(ex, "send");
        }
    }

    /// <summary>
    /// POST /api/otp/validate — validate a user-entered code for a phone number
    /// and application id. Returns 200 on success, 401 ProblemDetails on an
    /// invalid/expired code, 502 on an upstream fault.
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Validate([FromBody] OtpValidateRequest request, CancellationToken ct)
    {
        if (!_flags.CurrentValue.Otp)
            return UpstreamDisabled();

        if (request is null
            || string.IsNullOrWhiteSpace(request.PhoneNumber)
            || string.IsNullOrWhiteSpace(request.Otp)
            || string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            return Problem(
                title: "Invalid OTP validate request",
                detail: "phoneNumber, otp and applicationId are required.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1");
        }

        try
        {
            await _otpClient.ValidateOTPAsync(new ValidateOTPRequestModel
            {
                PhoneNumber = request.PhoneNumber!,
                Otp = request.Otp!,
                ApplicationId = request.ApplicationId!
            }, ct);

            _log.LogInformation(
                "OTP validated for applicationId {ApplicationId}", request.ApplicationId);

            return Ok();
        }
        catch (ApiException ex) when (ex.StatusCode is StatusCodes.Status401Unauthorized
                                          or StatusCodes.Status400BadRequest)
        {
            // The OTP service returns 401 for every validation failure (wrong
            // code, expired, no record, too-many-attempts) — surface as a 401
            // ProblemDetails without echoing the upstream body (which may embed
            // the submitted code).
            return Problem(
                title: "OTP validation failed",
                detail: "The provided one-time-password is invalid or has expired.",
                statusCode: StatusCodes.Status401Unauthorized,
                type: "https://datatracker.ietf.org/doc/html/rfc7235#section-3.1");
        }
        catch (ApiException ex)
        {
            return UpstreamFault(ex, "validate");
        }
    }

    /// <summary>
    /// 503 ProblemDetails when the OTP upstream kill switch is off. There is no
    /// in-memory fallback for a one-time-password service, so the surface fails
    /// closed rather than calling a localhost default.
    /// </summary>
    private ObjectResult UpstreamDisabled() => Problem(
        title: "OTP service not enabled",
        detail: "The one-time-password upstream is not enabled in this environment "
              + "(FeatureFlags:UseUpstream:Otp is false).",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.4");

    /// <summary>
    /// Maps an upstream <see cref="ApiException"/> to a 502 ProblemDetails. The
    /// upstream response body is NEVER forwarded to the caller or the log — it
    /// may contain OTP-adjacent data (PR review B5 precedent).
    /// </summary>
    private ObjectResult UpstreamFault(ApiException ex, string operation)
    {
        _log.LogWarning(
            "OTP {Operation} upstream failure: status {UpstreamStatus}",
            operation, ex.StatusCode);

        return Problem(
            title: "OTP service upstream failure",
            detail: $"The one-time-password service returned an unexpected status while handling '{operation}'.",
            statusCode: StatusCodes.Status502BadGateway,
            type: "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.3");
    }
}

/// <summary>
/// Gateway request body for <c>POST /api/otp/send</c> — the generic-upstream
/// contract <c>{ phoneNumber, applicationId }</c>. The phone sign-in flow has its
/// own surface at <c>POST /v1/auth/otp/request</c>
/// (<see cref="JeebGateway.Auth.OtpSignIn.AuthOtpController"/>); this generic
/// proxy no longer carries the old sign-in alias (the in-gateway OTP mock that
/// needed it was retired in JEB-1516).
/// </summary>
public sealed record OtpSendRequest(string? PhoneNumber, string? ApplicationId);

/// <summary>
/// Gateway request body for <c>POST /api/otp/validate</c> — the generic-upstream
/// contract <c>{ phoneNumber, otp, applicationId }</c>. The phone sign-in flow
/// has its own surface at <c>POST /v1/auth/otp/verify</c>
/// (<see cref="JeebGateway.Auth.OtpSignIn.AuthOtpController"/>); this generic
/// proxy no longer carries the old sign-in aliases (JEB-1516).
/// </summary>
public sealed record OtpValidateRequest(string? PhoneNumber, string? Otp, string? ApplicationId);
