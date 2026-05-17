using JeebGateway.Users;
using JeebGateway.Wallet;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("api/wallet")]
public sealed class WalletController : ControllerBase
{
    private readonly IInAppWalletService _wallet;

    public WalletController(IInAppWalletService wallet) => _wallet = wallet;

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
}
