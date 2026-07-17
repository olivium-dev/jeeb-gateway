using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.JeebWallet;
using JeebGateway.Partner;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeb Partner Portal wallet BFF (partner-wallet-bff). The portal a Partner (cash shop/agent) uses
/// to see its own wallet and top up jeeber wallets from the cash it holds:
///
/// <list type="bullet">
///   <item><c>GET  /v1/partner/wallet</c> — the partner's own balance/summary.</item>
///   <item><c>GET  /v1/partner/wallet/ledger</c> — the partner's own paginated transaction ledger.</item>
///   <item><c>POST /v1/partner/wallet/transfers/predict</c> — preview fees for a partner→jeeber top-up.</item>
///   <item><c>POST /v1/partner/wallet/transfers</c> — execute a partner→jeeber top-up.</item>
/// </list>
///
/// <para>ADR-0001 thin BFF: authenticates, resolves the caller's own holder id from the bearer,
/// delegates every money move to the reused wallet-service saga via <see cref="IPartnerWalletService"/>,
/// and returns. No state, no persistence, no money math (the gateway never derives a monetary value
/// — amounts are the caller's, fees are wallet-service's). Reads the balance class-cap
/// <c>partner.wallet.read.own</c>; the two money-mutating transfer actions override to
/// <c>partner.topup.execute</c> (ADR-005; every action carries a capability marker).</para>
/// </summary>
[Route("v1/partner/wallet")]
[RequireCapability(Capabilities.PartnerWalletReadOwn)]
public sealed class PartnerWalletController : PartnerControllerBase
{
    private readonly IPartnerWalletService _partner;
    private readonly IJeebWalletLedgerReader _ledger;
    private readonly PartnerWalletOptions _options;
    private readonly ILogger<PartnerWalletController> _log;

    public PartnerWalletController(
        IPartnerWalletService partner,
        IJeebWalletLedgerReader ledger,
        IOptions<PartnerWalletOptions> options,
        ILogger<PartnerWalletController> log)
    {
        _partner = partner;
        _ledger = ledger;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// GET /v1/partner/wallet (and the documented alias /v1/partner/wallet/balance) — the caller
    /// partner's own wallet balance/summary. Both routes resolve the SAME action so a consumer coding
    /// to either the shipped surface or the BUILD-REPORT §3.1 documented path never 404s (contract
    /// drift fix; pinned by PartnerWalletRouteContractTests).
    /// </summary>
    [HttpGet]
    [HttpGet("balance")]
    [ProducesResponseType(typeof(PartnerWalletBalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetBalance(CancellationToken ct)
    {
        if (!TryResolveCallerId(out var partnerId, out var failure)) return failure;
        try
        {
            return Ok(await _partner.GetPartnerBalanceAsync(partnerId, ct));
        }
        catch (WalletApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return Ok(new PartnerWalletBalanceResponse
            {
                PartnerId = partnerId,
                Balance = 0d,
                CurrencyId = _options.CurrencyId,
                IsActive = false,
            });
        }
        catch (WalletApiException ex)
        {
            return UpstreamProblem(ex, _log);
        }
    }

    /// <summary>GET /v1/partner/wallet/ledger — the caller partner's own paginated transaction ledger.</summary>
    [HttpGet("ledger")]
    [ProducesResponseType(typeof(JeebWalletLedgerPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLedger(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryResolveCallerId(out var partnerId, out var failure)) return failure;

        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;

        var items = await _ledger.ReadLedgerAsync(partnerId, safePage, safeSize, ct);
        var totalPages = items.Count >= safeSize ? safePage + 1 : safePage;

        return Ok(new JeebWalletLedgerPageResponse
        {
            Items = items,
            Page = safePage,
            TotalPages = totalPages < 1 ? 1 : totalPages,
        });
    }

    /// <summary>POST /v1/partner/wallet/transfers/predict — preview fees for a partner→jeeber top-up.</summary>
    [HttpPost("transfers/predict")]
    [RequireCapability(Capabilities.PartnerTopupExecute)]
    [ProducesResponseType(typeof(PartnerTopupPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Predict([FromBody] PartnerTopupPredictRequest body, CancellationToken ct)
    {
        if (!TryResolveCallerId(out var partnerId, out var failure)) return failure;
        if (!TryValidateAmount(body.Amount, out var amountProblem)) return amountProblem;

        try
        {
            return Ok(await _partner.PredictTopupAsync(partnerId, body.JeeberId, body.Amount, ct));
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

    /// <summary>POST /v1/partner/wallet/transfers — execute a partner→jeeber top-up (idempotency-keyed).</summary>
    [HttpPost("transfers")]
    [RequireCapability(Capabilities.PartnerTopupExecute)]
    [ProducesResponseType(typeof(PartnerWalletMoveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Execute([FromBody] PartnerTopupExecuteRequest body, CancellationToken ct)
    {
        if (!TryResolveCallerId(out var partnerId, out var failure)) return failure;
        if (!TryValidateAmount(body.Amount, out var amountProblem)) return amountProblem;

        try
        {
            var result = await _partner.ExecuteTopupAsync(
                partnerId, body.JeeberId, body.Amount, body.IdempotencyKey, body.Note, ct);
            return Ok(result);
        }
        catch (PartnerWalletInFlightException ex)
        {
            return InFlightProblem(ex);
        }
        catch (PartnerWalletUncertainException ex)
        {
            return UncertainProblem(ex, _log);
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

    /// <summary>Reject an amount above the configured ceiling BEFORE any wallet-service call.</summary>
    private bool TryValidateAmount(double amount, out IActionResult problem)
        => TryEnforceAmountCeiling(amount, _options.MaxTransferAmount, out problem);
}
