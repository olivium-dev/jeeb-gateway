namespace JeebGateway.TestControlPlane;

/// <summary>
/// JEB-1502: A registered job that can be force-run through the test
/// control-plane's <c>POST /__test/jobs/{name}/run</c> endpoint.
/// </summary>
public sealed class RegisteredJob
{
    /// <summary>
    /// Stable, URL-safe, lowercase name used in the route (e.g. <c>rating-reveal</c>).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Human description surfaced by <c>GET /__test/jobs</c>.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The delegate that executes one sweep.
    /// MUST be the same code path the background host runs — no test-only forks.
    /// </summary>
    public Func<CancellationToken, Task> RunAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>UTC time of the last forced invocation, or <c>null</c> if never forced.</summary>
    public DateTimeOffset? LastForcedAt { get; set; }

    /// <summary>UTC time of the last scheduled invocation, or <c>null</c> if never run.</summary>
    public DateTimeOffset? LastScheduledAt { get; set; }
}

/// <summary>
/// JEB-1502: Registry of background jobs that the test control-plane can
/// force-trigger.
///
/// <para>
/// Background services register themselves at startup by calling
/// <see cref="Register"/>. The control-plane controller calls
/// <see cref="RunAsync"/> to execute one sweep on demand.
/// </para>
///
/// <para>
/// Thread safety: <see cref="Register"/> is called at startup (single-threaded
/// DI setup) so the dictionary itself never mutates after that; only
/// <see cref="RegisteredJob.LastForcedAt"/> is mutated at run-time, which is a
/// single reference assignment (safe without locking for the diagnostic read
/// in <c>GET /__test/jobs</c>).
/// </para>
/// </summary>
public interface ITestJobRegistry
{
    /// <summary>
    /// Register a job. Called once at startup by the hosted service or its
    /// registration code.
    /// </summary>
    void Register(RegisteredJob job);

    /// <summary>
    /// Returns all registered jobs, sorted by name.
    /// </summary>
    IReadOnlyList<RegisteredJob> List();

    /// <summary>
    /// Force-run the job with the given <paramref name="name"/> exactly once,
    /// through its normal sweep code path.
    /// </summary>
    /// <returns><c>true</c> if the job was found and run; <c>false</c> if not found.</returns>
    Task<bool> RunAsync(string name, CancellationToken ct);
}

/// <summary>Default implementation of <see cref="ITestJobRegistry"/>.</summary>
public sealed class TestJobRegistry : ITestJobRegistry
{
    private readonly Dictionary<string, RegisteredJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public void Register(RegisteredJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.Name))
            throw new ArgumentException("Job name must be non-empty.", nameof(job));
        _jobs[job.Name] = job;
    }

    public IReadOnlyList<RegisteredJob> List()
        => _jobs.Values.OrderBy(j => j.Name).ToList();

    public async Task<bool> RunAsync(string name, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(name, out var job)) return false;
        await job.RunAsync(ct);
        job.LastForcedAt = DateTimeOffset.UtcNow;
        return true;
    }
}
