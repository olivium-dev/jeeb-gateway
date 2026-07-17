using System;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Partner;

/// <summary>
/// Shared thin base for the Partner Portal controllers: caller-id resolution and the sanitized
/// RFC 7807 mappings for upstream wallet-service errors and gateway-side partner preconditions.
/// Mirrors the JeebWalletController / WalletController UpstreamProblem idiom (JEBV4-249/253) — the
/// upstream body is logged server-side ONLY, never echoed to the caller (no info disclosure).
/// </summary>
[ApiController]
[Produces("application/json", "application/problem+json")]
public abstract class PartnerControllerBase : ControllerBase
{
    /// <summary>Resolve the caller's own wallet-holder id (their user GUID) from the bearer token.</summary>
    protected bool TryResolveCallerId(out Guid callerId, out IActionResult failure)
    {
        callerId = Guid.Empty;

        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            failure = unauthorized;
            return false;
        }

        if (!Guid.TryParse(userId, out callerId))
        {
            failure = StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Caller identity is not a valid wallet-holder id.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/invalid-wallet-holder",
            });
            return false;
        }

        failure = null!;
        return true;
    }

    /// <summary>Map an upstream wallet-service failure to a sanitized RFC 7807 result (body never echoed).</summary>
    protected IActionResult UpstreamProblem(WalletApiException ex, ILogger log)
    {
        var status = ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway;
        log.LogWarning(ex,
            "Partner wallet BFF: wallet-service call failed on {Method} {Path} → {Status}.",
            Request.Method, Request.Path, status);
        return Problem(title: "The wallet request could not be completed.", statusCode: status);
    }

    /// <summary>
    /// Map a gateway-side precondition failure (missing wallet, un-executable saga) to a 409 the
    /// caller can act on. Carries the gateway-authored message only (no upstream body).
    /// </summary>
    protected IActionResult PartnerProblem(PartnerWalletException ex)
        => Problem(
            title: ex.Message,
            statusCode: StatusCodes.Status409Conflict,
            type: "https://jeeb.dev/errors/partner-wallet-precondition");
}
