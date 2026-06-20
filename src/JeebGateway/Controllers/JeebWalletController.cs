using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using JeebGateway.JeebWallet;
using Microsoft.AspNetCore.Mvc;
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// The Jeeb-mapped WALLET BFF surface the mobile app consumes
/// (<c>DioWalletRepository</c> / <c>DioWalletLedgerRepository</c> /
/// <c>DioWalletTransactionRepository</c>):
///
/// <list type="bullet">
///   <item><c>GET /v1/jeeb/wallet</c> — balance / summary.</item>
///   <item><c>GET /v1/jeeb/wallet/ledger</c> — paginated transaction list.</item>
///   <item><c>GET /v1/jeeb/wallet/ledger/{id}</c> — a single transaction.</item>
/// </list>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): this controller authenticates, resolves the
/// caller's own wallet-holder id from the bearer token, maps to the EXISTING
/// generic <see cref="ServiceWalletClient"/>, applies the Jeeb presentation
/// projection (<see cref="JeebWalletProjection"/>), and returns. It holds NO
/// state, NO persistence, NO session, and NO domain rules — money never moves
/// here and balances are never stored. The generic wallet-service stays
/// product-agnostic; all Jeeb vocabulary lives in the gateway projection (GR2),
/// mirroring <see cref="JeebRatingsController"/>.
/// </para>
///
/// <para>
/// Coverage note: the generic <see cref="ServiceWalletClient"/> exposes a
/// holder-wallets read (the balance source) but NO Jeeb-shaped ledger LIST or
/// single-ledger-entry read. Until that generic read exists, the ledger routes
/// return the correctly-shaped EMPTY page / 404 the mobile parsers already
/// tolerate, rather than fabricating ledger state in the gateway (which ADR-0001
/// forbids). Re-point these to the generic ledger read the moment wallet-service
/// ships it — the mobile-facing contract here does not change.
/// </para>
/// </summary>
[ApiController]
[Route("v1/jeeb/wallet")]
[RequireCapability(Capabilities.WalletReadOwn)]
public sealed class JeebWalletController : ControllerBase
{
    private readonly ServiceWalletClient _wallet;

    public JeebWalletController(ServiceWalletClient wallet)
    {
        _wallet = wallet;
    }

    /// <summary>
    /// GET /v1/jeeb/wallet — the caller's own wallet balance/summary, projected
    /// from the generic holder-wallets read.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(JeebWalletBalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetBalance(CancellationToken ct)
    {
        if (!TryResolveHolderId(out var holderId, out var failure)) return failure;

        try
        {
            var holder = await _wallet.WalletsAsync(holderId, ct);
            return Ok(JeebWalletProjection.ProjectBalance(holder));
        }
        catch (WalletApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            // No holder/wallet provisioned yet → an empty wallet, not an error
            // (matches the mobile "empty" affordability default).
            return Ok(JeebWalletProjection.ProjectBalance(null));
        }
        catch (WalletApiException ex)
        {
            return UpstreamProblem("Upstream wallet-service rejected the balance read.", ex);
        }
    }

    /// <summary>
    /// GET /v1/jeeb/wallet/ledger?page=&amp;pageSize= — the caller's own paginated
    /// transaction ledger.
    /// </summary>
    [HttpGet("ledger")]
    [ProducesResponseType(typeof(JeebWalletLedgerPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public Task<IActionResult> GetLedger(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryResolveHolderId(out _, out var failure)) return Task.FromResult(failure);

        var safePage = page < 1 ? 1 : page;

        // The generic wallet client has no Jeeb-shaped ledger LIST read yet; return
        // the empty, correctly-shaped page the mobile ledger parser tolerates rather
        // than synthesise ledger rows in the (stateless) gateway. See class remarks.
        IActionResult ok = Ok(new JeebWalletLedgerPageResponse
        {
            Items = Array.Empty<JeebWalletLedgerEntry>(),
            Page = safePage,
            TotalPages = 1,
        });
        return Task.FromResult(ok);
    }

    /// <summary>
    /// GET /v1/jeeb/wallet/ledger/{id} — a single transaction by ledger id.
    /// </summary>
    [HttpGet("ledger/{id}")]
    [ProducesResponseType(typeof(JeebWalletLedgerEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetLedgerEntry(string id, CancellationToken ct)
    {
        if (!TryResolveHolderId(out _, out var failure)) return Task.FromResult(failure);

        // No generic single-ledger-entry read exists; 404 is the honest, mobile-mapped
        // response (DioWalletTransactionRepository maps 404 → notFound) until the
        // generic read lands. See class remarks.
        IActionResult notFound = NotFound();
        return Task.FromResult(notFound);
    }

    /// <summary>
    /// Resolve the caller's wallet-holder id (their own user GUID) from the bearer
    /// token. Returns a populated 401/403 result on failure. Scoping the read to the
    /// caller's own id is request-scoped logic (no stored state) — ADR-0001 clean.
    /// </summary>
    private bool TryResolveHolderId(out Guid holderId, out IActionResult failure)
    {
        holderId = Guid.Empty;

        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            failure = unauthorized;
            return false;
        }

        if (!Guid.TryParse(userId, out holderId))
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

    private IActionResult UpstreamProblem(string title, WalletApiException ex) => Problem(
        title: title,
        detail: ex.Message,
        statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway);
}
