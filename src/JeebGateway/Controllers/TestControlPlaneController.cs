using JeebGateway.Auth.Capabilities;
using JeebGateway.TestControlPlane;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// JEB-1502 — Test control-plane: clock manipulation + job force-runner.
///
/// <para>
/// <b>Security model (defence in depth):</b>
/// <list type="bullet">
///   <item>nginx does NOT proxy the <c>/__test</c> prefix → never internet-reachable
///         even if the in-app flag is misconfigured (WS-E smoke probe verifies).</item>
///   <item><c>TestControlPlane:Enabled=false</c> by default → every route is 404
///         on production without an explicit env override.</item>
///   <item><c>X-Test-Control-Plane-Secret</c> header must match the configured
///         shared secret → 401 on mismatch regardless of how the flag is set.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>No test-only logic forks.</b> <c>POST /__test/jobs/{name}/run</c> runs jobs
/// through the exact same code path as the background scheduler — the delegate
/// registered in <see cref="ITestJobRegistry"/> IS the sweep method.
/// </para>
/// </summary>
[ApiController]
[Route("__test")]
[TestControlPlaneOnly]
[AllowAnonymous]
[Produces("application/json")]
// ADR-005 §A: config-gated test seam; same exemption category as DevController.
[PublicEndpoint("Config-gated test control-plane ([TestControlPlaneOnly]) — JEB-1502.")]
public sealed class TestControlPlaneController : ControllerBase
{
    private readonly FakeTimeProvider _clock;
    private readonly ITestJobRegistry _jobs;
    private readonly ILogger<TestControlPlaneController> _log;

    public TestControlPlaneController(
        FakeTimeProvider clock,
        ITestJobRegistry jobs,
        ILogger<TestControlPlaneController> log)
    {
        _clock = clock;
        _jobs = jobs;
        _log = log;
    }

    // -------------------------------------------------------------------------
    // Clock endpoints
    // -------------------------------------------------------------------------

    /// <summary>
    /// Advance the app-wide clock by <c>durationSeconds</c>. The advance is
    /// additive — multiple advances compose (advance 60 s, then 120 s → +180 s
    /// total). The FakeTimeProvider wraps the ambient system clock; jobs that
    /// read <see cref="TimeProvider.GetUtcNow()"/> will observe the shifted now.
    /// </summary>
    [HttpPost("clock/advance")]
    [ProducesResponseType(typeof(ClockStateResponse), StatusCodes.Status200OK)]
    public IActionResult ClockAdvance([FromBody] ClockAdvanceRequest request)
    {
        var shifted = _clock.AdvanceBy(TimeSpan.FromSeconds(request.DurationSeconds));
        _log.LogInformation(
            "JEB-1502 test-clock advanced by {Seconds}s — effective now {Now:O} (offset {Offset})",
            request.DurationSeconds, shifted, _clock.CurrentOffset);
        return Ok(new ClockStateResponse(shifted, _clock.CurrentOffset));
    }

    /// <summary>
    /// Reset the clock offset to zero. Wall-clock behaviour is restored.
    /// </summary>
    [HttpPost("clock/reset")]
    [ProducesResponseType(typeof(ClockStateResponse), StatusCodes.Status200OK)]
    public IActionResult ClockReset()
    {
        _clock.Reset();
        var now = _clock.GetUtcNow();
        _log.LogInformation("JEB-1502 test-clock reset — effective now {Now:O}", now);
        return Ok(new ClockStateResponse(now, _clock.CurrentOffset));
    }

    /// <summary>
    /// Return the current effective UTC now and the active offset. Useful for
    /// diagnostic / assertion in test scenarios.
    /// </summary>
    [HttpGet("clock")]
    [ProducesResponseType(typeof(ClockStateResponse), StatusCodes.Status200OK)]
    public IActionResult ClockGet()
        => Ok(new ClockStateResponse(_clock.GetUtcNow(), _clock.CurrentOffset));

    // -------------------------------------------------------------------------
    // Job endpoints
    // -------------------------------------------------------------------------

    /// <summary>
    /// Force-run a registered background job once. The job executes through the
    /// SAME code path as the background scheduler — no test-only forks. Returns
    /// 404 if the job name is not in the registry.
    /// </summary>
    [HttpPost("jobs/{name}/run")]
    [ProducesResponseType(typeof(JobRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> JobRun([FromRoute] string name, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = await _jobs.RunAsync(name, ct);
        sw.Stop();

        if (!found)
        {
            return Problem(
                title: "Job not found",
                detail: $"No job named '{name}' is registered. Available: {string.Join(", ", _jobs.List().Select(j => j.Name))}",
                statusCode: StatusCodes.Status404NotFound);
        }

        _log.LogInformation("JEB-1502 force-ran job '{Name}' in {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
        return Ok(new JobRunResponse(name, sw.Elapsed));
    }

    /// <summary>
    /// List all registered jobs with their last forced/scheduled run timestamps.
    /// </summary>
    [HttpGet("jobs")]
    [ProducesResponseType(typeof(JobListResponse), StatusCodes.Status200OK)]
    public IActionResult JobList()
        => Ok(new JobListResponse(_jobs.List().Select(j => new JobView(
            j.Name, j.Description, j.LastForcedAt, j.LastScheduledAt)).ToList()));
}

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public sealed record ClockAdvanceRequest(int DurationSeconds);

public sealed record ClockStateResponse(
    DateTimeOffset EffectiveNow,
    TimeSpan Offset);

public sealed record JobRunResponse(
    string Name,
    TimeSpan Elapsed);

public sealed record JobView(
    string Name,
    string Description,
    DateTimeOffset? LastForcedAt,
    DateTimeOffset? LastScheduledAt);

public sealed record JobListResponse(IReadOnlyList<JobView> Jobs);
