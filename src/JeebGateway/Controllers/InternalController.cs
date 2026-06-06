using JeebGateway.Auth.Capabilities;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Internal endpoints protected by API key authentication (T-backend-032).
/// Used by downstream services for health probing and diagnostics.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("internal")]
// ADR-004 D1: public by design — internal liveness probe, must answer without a token.
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
// ADR-005 §A public.
[PublicEndpoint("Internal gateway liveness probe — ADR-005 §A public.")]
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
