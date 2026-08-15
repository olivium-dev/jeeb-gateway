using JeebGateway.Auth.Capabilities;
using JeebGateway.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("internal/jobs")]
[AllowAnonymous]
[PublicEndpoint("Deployment-service-token authenticated durable job executor; never accepts user bearer auth.")]
[ServiceFilter(typeof(InternalJobTokenAuthorizationFilter))]
public sealed class InternalDurableJobsController(DurableWorkSweepExecutor executor) : ControllerBase
{
    [HttpPost("account-deletions/sweep")]
    [ProducesResponseType(typeof(DurableSweepSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<DurableSweepSummary>> SweepAccountDeletions(
        [FromQuery] int? limit,
        CancellationToken ct) =>
        Ok(await executor.SweepAsync(DurableWorkContract.AccountDeletionKind, limit, ct));

    [HttpPost("data-exports/sweep")]
    [ProducesResponseType(typeof(DurableSweepSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<DurableSweepSummary>> SweepDataExports(
        [FromQuery] int? limit,
        CancellationToken ct) =>
        Ok(await executor.SweepAsync(DurableWorkContract.DataExportKind, limit, ct));
}
