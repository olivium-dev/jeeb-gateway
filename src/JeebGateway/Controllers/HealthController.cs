using JeebGateway.Auth.Capabilities;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("api/[controller]")]
// ADR-004 D1: public by design — liveness probe, must answer without a token.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
// ADR-005 §A public: bypasses both layers. [AllowAnonymous] opts out of L1; [PublicEndpoint] opts out
// of L2 and satisfies the default-deny coverage guard.
[PublicEndpoint("Gateway liveness probe — ADR-005 §A public.")]
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
