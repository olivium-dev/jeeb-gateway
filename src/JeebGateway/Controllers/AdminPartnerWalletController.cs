using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Partner;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeb Partner Portal admin cash-credit BFF (partner-wallet-bff). The operator surface for step 1 of
/// the cash-only model: a partner hands cash to Jeeb offline, and a Jeeb ADMIN records it and credits
/// the partner wallet.
///
/// <list type="bullet">
///   <item><c>POST /v1/admin/partners/{partnerId}/wallet/credits</c> — record offline cash and credit
///   the partner wallet (mandatory evidence note + audit trail).</item>
/// </list>
///
/// <para>ADR-0001 thin BFF: the credit itself is a wallet-service saga move (system → partner wallet)
/// via <see cref="IPartnerWalletService"/> — the gateway never writes a balance or computes money.
/// Admin-only capability <c>partner.wallet.credit</c> (ADR-005 marker). The evidence note is validated
/// mandatory (DataAnnotations → 400 problem+json) and the operator id + note are written to the audit
/// log for every credit.</para>
/// </summary>
[Route("v1/admin/partners")]
[RequireCapability(Capabilities.PartnerWalletCredit)]
public sealed class AdminPartnerWalletController : PartnerControllerBase
{
    private readonly IPartnerWalletService _partner;
    private readonly ILogger<AdminPartnerWalletController> _log;

    public AdminPartnerWalletController(IPartnerWalletService partner, ILogger<AdminPartnerWalletController> log)
    {
        _partner = partner;
        _log = log;
    }

    /// <summary>POST /v1/admin/partners/{partnerId}/wallet/credits — admin records offline cash.</summary>
    [HttpPost("{partnerId:guid}/wallet/credits")]
    [ProducesResponseType(typeof(PartnerWalletMoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreditPartner(
        Guid partnerId,
        [FromBody] PartnerCashCreditRequest body,
        CancellationToken ct)
    {
        // Resolve the acting admin for the audit trail (Layer 1/2 already enforced admin capability).
        UserIdentity.TryGetUserId(HttpContext, out var operatorId, out _);
        _log.LogInformation(
            "Admin partner cash-credit request: operator={OperatorId} partner={PartnerId} amount={Amount} evidence={Evidence}",
            operatorId, partnerId, body.Amount, body.EvidenceNote);

        try
        {
            var result = await _partner.CreditPartnerFromCashAsync(partnerId, body.Amount, body.EvidenceNote, ct);
            return Ok(result);
        }
        catch (PartnerWalletException ex)
        {
            return PartnerProblem(ex);
        }
        catch (WalletApiException ex)
        {
            return UpstreamProblem(ex, _log);
        }
    }
}
