using JeebGateway.Services.Clients;
using JeebGateway.Users;
using JeebGateway.Wallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("api/wallet")]
public sealed class WalletController : ControllerBase
{
    private readonly IInAppWalletService _wallet;
    private readonly IWalletServiceClient _walletClient;

    public WalletController(IInAppWalletService wallet, IWalletServiceClient walletClient)
    {
        _wallet = wallet;
        _walletClient = walletClient;
    }

    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var balance = await _wallet.GetBalanceAsync(userId, ct);
        return Ok(balance);
    }

    [HttpPost("top-up")]
    public async Task<IActionResult> TopUp(
        [FromBody] TopUpRequest request,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var result = await _wallet.TopUpAsync(
            request with { UserId = userId }, ct);
        return result.Success ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var problem))
            return problem;

        var txns = await _wallet.GetTransactionsAsync(userId, page, pageSize, ct);
        return Ok(txns);
    }

    /// <summary>
    /// Returns the system wallet holder and all its wallets as recorded in
    /// the jeeb-wallet Postgres database via wallet-service
    /// <c>GET /system-wallet</c>. Requires an authenticated caller.
    ///
    /// When <c>Services:Wallet:BaseUrl</c> is not configured the in-memory
    /// fallback returns 404 so callers know no real data is available.
    /// </summary>
    [HttpGet("system")]
    [Authorize]
    [ProducesResponseType(typeof(SystemWalletResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSystemWallet(CancellationToken ct)
    {
        var result = await _walletClient.GetSystemWalletAsync(ct);
        return result is null
            ? NotFound(new { message = "System wallet not found or wallet-service unavailable." })
            : Ok(result);
    }
}
