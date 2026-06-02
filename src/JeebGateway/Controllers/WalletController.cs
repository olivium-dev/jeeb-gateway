using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.service.ServiceWallet;
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly ServiceWalletClient _walletClient;

        public WalletController(ServiceWalletClient walletClient)
        {
            _walletClient = walletClient;
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Internal server error: " + ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Internal server error: " + ex.Message);
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("holder/wallets")]
        [Authorize]
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
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
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
