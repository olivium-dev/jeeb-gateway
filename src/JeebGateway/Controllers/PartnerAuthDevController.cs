using System;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Partner.Auth;
using JeebGateway.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// <b>[DevOnly]</b> partner-credential seed seam for the test harness / local runs:
/// <c>POST /dev/partner/credentials</c>. Lets a scenario provision a partner without a committed
/// config roster, so an end-to-end "log in → top up" flow can run against a fresh host.
///
/// <para><b>Never a production surface.</b> The whole controller carries <see cref="DevOnlyAttribute"/>:
/// when <c>Features:DevEndpoints:Enabled</c> is false (the committed value in EVERY environment,
/// including production) every action 404s — indistinguishable from a route that does not exist. This
/// mirrors <see cref="DevController"/> exactly; the seeded credential lives only in the in-memory
/// singleton store for the host's lifetime.</para>
/// </summary>
[DevOnly]
[ApiController]
[Route("dev/partner")]
[Produces("application/json")]
// Config-gated dev seam ([DevOnly]) — anonymous-by-design, bypasses L2 (mirrors DevController; ADR-005 §A).
[AllowAnonymous]
[PublicEndpoint("Config-gated [DevOnly] partner-credential seed seam — ADR-005 §A public.")]
public sealed class PartnerAuthDevController : ControllerBase
{
    private readonly IPartnerCredentialStore _credentials;
    private readonly ILogger<PartnerAuthDevController> _log;

    public PartnerAuthDevController(IPartnerCredentialStore credentials, ILogger<PartnerAuthDevController> log)
    {
        _credentials = credentials;
        _log = log;
    }

    /// <summary>POST /dev/partner/credentials — provision (or overwrite) a partner login credential at runtime.</summary>
    [HttpPost("credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SeedCredential([FromBody] PartnerAuthDevSeedRequest request)
    {
        // [ApiController] 400s missing/blank fields; validate the holder id shape here.
        if (!Guid.TryParse(request.HolderId, out var holderId))
        {
            return Problem(
                title: "Invalid holderId.",
                detail: "holderId must be a GUID (the partner's user-management userId).",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://jeeb.dev/errors/invalid-holder-id");
        }

        _credentials.Seed(request.Identifier, holderId, request.DisplayName, request.Password);
        _log.LogInformation("partner.auth.dev seeded a partner credential (holder set).");
        return NoContent();
    }
}
