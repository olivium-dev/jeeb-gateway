using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Partner;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeb Partner Portal jeeber-lookup BFF (partner-wallet-bff). The read-only step a partner performs
/// to pick a top-up destination before choosing an amount:
///
/// <list type="bullet">
///   <item><c>GET /v1/partner/jeebers/{jeeberId}/wallet-target</c> — is this jeeber a valid top-up
///   destination (does it have a provisioned wallet)?</item>
/// </list>
///
/// <para>ADR-0001 thin BFF: resolves the target against the reused wallet-service holder read only —
/// no state, no money math. Capability <c>partner.jeeber.lookup</c> ({partner}) on the single action
/// (ADR-005 marker).</para>
///
/// <para>NOTE: identifier-based lookup resolves an already-known jeeber GUID (e.g. from a scanned
/// jeeber QR / shared id). Free-text jeeber SEARCH by phone/name is a user-management concern and is
/// deliberately out of scope here until a user-management typed client is generated into the gateway
/// (see report TODO) — this BFF only touches wallet-service.</para>
/// </summary>
[Route("v1/partner/jeebers")]
[RequireCapability(Capabilities.PartnerJeeberLookup)]
public sealed class PartnerJeebersController : PartnerControllerBase
{
    private readonly IPartnerWalletService _partner;
    private readonly ILogger<PartnerJeebersController> _log;

    public PartnerJeebersController(IPartnerWalletService partner, ILogger<PartnerJeebersController> log)
    {
        _partner = partner;
        _log = log;
    }

    /// <summary>GET /v1/partner/jeebers/{jeeberId}/wallet-target — validate a jeeber top-up destination.</summary>
    [HttpGet("{jeeberId:guid}/wallet-target")]
    [ProducesResponseType(typeof(PartnerJeeberTargetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetWalletTarget(Guid jeeberId, CancellationToken ct)
    {
        // Caller must be an identified partner (Layer 1/2 already enforced); resolve to fail closed
        // on a malformed identity even though the id is not otherwise used for this read.
        if (!TryResolveCallerId(out _, out var failure)) return failure;

        try
        {
            return Ok(await _partner.ResolveJeeberTargetAsync(jeeberId, ct));
        }
        catch (WalletApiException ex)
        {
            return UpstreamProblem(ex, _log);
        }
    }
}
