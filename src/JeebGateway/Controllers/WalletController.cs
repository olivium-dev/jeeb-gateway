using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.Auth.Capabilities;
using JeebGateway.service.ServiceWallet;
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // ADR-005 L2 §K admin (OPEN-2 baked): wallet holder/wallet management (create/add/deactivate/
    // force-deactivate/system-wallet) is admin-only. These actions carried no explicit user-type gate
    // (only the L1 fallback identified-caller); declaring {admin} is the documented ADR posture. The
    // caller's OWN wallet read (holder/wallets) overrides to wallet.read.own ({client, jeeber}) below.
    [RequireCapability(Capabilities.WalletManage)]
    public class WalletController : ControllerBase
    {
        private readonly ServiceWalletClient _walletClient;
        private readonly ILogger<WalletController> _logger;

        public WalletController(ServiceWalletClient walletClient, ILogger<WalletController> logger)
        {
            _walletClient = walletClient;
            _logger = logger;
        }

        /// <summary>
        /// JEBV4-253 — map a caught upstream <see cref="WalletApiException"/> to a
        /// sanitized RFC 7807 result. The upstream status is preserved (clamped to a
        /// valid 4xx/5xx; anything else becomes 502 Bad Gateway), but the upstream
        /// exception message / response body is logged server-side ONLY, never echoed
        /// to the caller. The JEBV4-63 relay still put <c>ex.Message</c> in the
        /// ProblemDetails <c>detail</c> field (the NSwag message embeds up to 512 chars
        /// of the raw upstream body) — this drops it. Mirrors
        /// <c>ChatController.UpstreamProblem</c> (JEBV4-242).
        /// </summary>
        private ActionResult UpstreamProblem(WalletApiException ex)
        {
            var status = ex.StatusCode is >= 400 and < 600
                ? ex.StatusCode
                : StatusCodes.Status502BadGateway;

            _logger.LogWarning(ex,
                "Wallet BFF: wallet-service call failed on {Method} {Path} → {Status}.",
                Request.Method, Request.Path, status);

            return Problem(
                title: "Upstream wallet-service error",
                statusCode: status);
        }

        [HttpGet("system-wallet")]
        [ProducesResponseType(typeof(AddWalletHolderResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetSystemWallet()
        {
            try
            {
                var systemWalletHolderResponse = await _walletClient.SystemWalletAsync();
                return Ok(systemWalletHolderResponse);
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }


        [HttpPost("holder/add")]
        [ProducesResponseType(typeof(AddWalletHolderResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CreateWalletOwner([FromBody] CreateWalletOwnerDto request)
        {
            try
            {
                var response = await _walletClient.AddAsync(request);
                return Ok(response);
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("holder/{holderId}/Add")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(Wallet))]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AddWalletToOwner(Guid holderId, [FromBody] AddWalletRequest addWalletRequest)
        {
            try
            {
                var response = await _walletClient.AddAsync(holderId, addWalletRequest);
                return Ok(response);
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("{holderId}/{walletId}/deactivate")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeactivateWallet(Guid holderId, Guid walletId)
        {
            try
            {
                await _walletClient.DeactivateAsync(holderId, walletId);
                return Accepted();
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("{holderId}/{walletId}/deactivate/force-deactivate")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ForceDeactivateWallet(Guid holderId, Guid walletId)
        {
            try
            {
                await _walletClient.ForceDeactivateAsync(holderId, walletId);
                return Accepted();
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("holder/{holderId}/deactivate")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeactivateWalletHolder(Guid holderId)
        {
            try
            {
                await _walletClient.Deactivate2Async(holderId);
                return Accepted();
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpPost("holder/{holderId}/deactivate/force-deactivate")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ForceDeactivateWalletHolder(Guid holderId)
        {
            try
            {
                await _walletClient.ForceDeactivate2Async(holderId);
                return Accepted();
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        [HttpGet("holder/wallets")]
        [Authorize]
        // ADR-005 L2 §H–J: the caller's OWN wallets — wallet.read.own {client, jeeber} (overrides the
        // class-level admin cap). Scoping to the caller's id stays STATE in-action.
        [RequireCapability(Capabilities.WalletReadOwn)]
        [ProducesResponseType(typeof(GetHolderWallets), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetHolderWallets()
        {
            try
            {
                var userId = GetUserIdFromToken();
                if (string.IsNullOrEmpty(userId))
                {
                    throw new Exception("User ID not found in token");
                }

                var wallets = await _walletClient.WalletsAsync(Guid.Parse(userId));
                return Ok(wallets);
            }
            catch (WalletApiException ex)
            {
                return UpstreamProblem(ex);
            }
        }

        private string GetUserIdFromToken()
        {
            var userId = User.FindFirst(ClaimTypes.Sid)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                userId = User.FindFirst("sid")?.Value;
            }
            if (string.IsNullOrEmpty(userId))
            {
                userId = User.FindFirst("sub")?.Value;
            }
            if (string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedAccessException("User ID not found in token");
            }
            return userId;
        }
    }
}
