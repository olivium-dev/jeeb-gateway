using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Internal endpoints protected by API key authentication (T-backend-032).
/// Used by downstream services for health probing and diagnostics.
/// </summary>
[ApiController]
[Route("internal")]
public class InternalController : ControllerBase
{
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            service = "jeeb-gateway",
            tier = "internal",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
