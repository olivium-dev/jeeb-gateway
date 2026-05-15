using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Shallow health probe for load-balancers that don't use /health/live.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "healthy",
            service = "jeeb-gateway",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
