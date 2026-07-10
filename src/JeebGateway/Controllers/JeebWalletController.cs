using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using JeebGateway.JeebWallet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    private readonly IJeebWalletLedgerReader _ledger;
    private readonly ILogger<JeebWalletController> _log;

    public JeebWalletController(
        ServiceWalletClient wallet,
        IJeebWalletLedgerReader ledger,
        ILogger<JeebWalletController> log)
    {
        _wallet = wallet;
        _ledger = ledger;
        _log = log;
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
            return UpstreamProblem(ex);
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
    public async Task<IActionResult> GetLedger(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryResolveHolderId(out var holderId, out var failure)) return failure;

        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > 200 ? 20 : pageSize;

        // REALAPP fix — the wallet-service has no Jeeb-shaped ledger LIST endpoint, so
        // read the holder's OWN transactions directly from the wallet DB
        // (transactionheader + transactiondetails, joined via wallets.holderid) and
        // project them into the mobile-facing ledger page. Read-only, request-scoped,
        // no money moves (ADR-0001 spirit). A no-data holder / DB blip degrades to the
        // empty, correctly-shaped page the mobile ledger parser tolerates (see
        // PostgresJeebWalletLedgerReader) — never a 5xx.
        var items = await _ledger.ReadLedgerAsync(holderId, safePage, safeSize, ct);

        // The page-count is best-effort over the returned page: the mobile parser only
        // needs items + a >=1 totalPages; a full COUNT(*) round-trip is unnecessary
        // chatter for a non-critical surface. A full page implies more may follow.
        var totalPages = items.Count >= safeSize ? safePage + 1 : safePage;

        return Ok(new JeebWalletLedgerPageResponse
        {
            Items = items,
            Page = safePage,
            TotalPages = totalPages < 1 ? 1 : totalPages,
        });
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

    /// <summary>
    /// JEBV4-249 — map a caught upstream wallet-service <see cref="WalletApiException"/>
    /// to a sanitized RFC 7807 <c>ProblemDetails</c>. The upstream status is preserved
    /// (clamped to a valid 4xx/5xx; anything else → 502 Bad Gateway), but the upstream
    /// exception message / response body is NEVER echoed to the caller — it is logged
    /// server-side only. Mirrors the JEBV4-242 <c>ChatController.UpstreamProblem</c> idiom.
    /// (Previously echoed the raw upstream <c>ex.Message</c> in the response detail — an
    /// info-disclosure leak of the wrapped upstream body.)
    /// </summary>
    private IActionResult UpstreamProblem(WalletApiException ex)
    {
        var status = ex.StatusCode is >= 400 and < 600
            ? ex.StatusCode
            : StatusCodes.Status502BadGateway;

        _log.LogWarning(ex,
            "Wallet BFF: wallet-service call failed on {Method} {Path} → {Status}.",
            Request.Method, Request.Path, status);

        return Problem(
            title: "The wallet request could not be completed.",
            statusCode: status);
    }
}
