using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users;

// ── Options ──────────────────────────────────────────────────────────────────

public sealed class AccountDeletionPurgeOptions
{
    public const string SectionName = "AccountDeletionPurge";

    /// <summary>
    /// How often the worker sweeps <c>account_deletions</c> for rows to advance
    /// (<c>pending_active_delivery</c> → <c>scheduled</c>, once the user's active
    /// deliveries clear) or purge (<c>scheduled</c> rows whose 30-day SLA
    /// — <see cref="InMemoryAccountDeletionStore.PurgeDelay"/> — is due →
    /// <c>completed</c>). Defaults hourly: frequent enough that the SLA is never
    /// meaningfully overshot, infrequent enough to keep the two partial-index
    /// sweeps (<c>account_deletions_pending_idx</c> / <c>account_deletions_due_idx</c>,
    /// migration 0010) cheap.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
}

// ── Hosted worker ──────────────────────────────────────────────────────────────

/// <summary>
/// Durable-store purge worker for the account-deletion lifecycle (T-backend-035).
/// Mirrors <see cref="JeebGateway.Availability.AutoOfflineSweeper"/>'s shape exactly:
/// a <see cref="BackgroundService"/> that ticks on a configured interval, resolves
/// its collaborators from a fresh <see cref="IServiceScope"/> per tick (so it never
/// captures a dependency at a narrower lifetime than its own), and exposes a public
/// force-runnable entry point (<see cref="PurgeOnceAsync"/>) for tests / an eventual
/// test-console hook — the same role <c>SweepOnceAsync</c> /
/// <c>WeeklySettlementBatch.RunBatchAsync</c> play for their stores.
///
/// Registered ONLY when <c>GatewayPostgres:ConnectionString</c> is configured (i.e.
/// alongside <see cref="PostgresAccountDeletionStore"/>) — there is no equivalent
/// worker for <see cref="InMemoryAccountDeletionStore"/> today (nothing currently
/// calls <see cref="IAccountDeletionStore.AdvanceAsync"/> in production), so adding
/// this worker is additive and does not change the in-memory fallback's behavior.
///
/// All the actual state-machine logic — including which side effects run exactly
/// once — lives in <see cref="IAccountDeletionStore.AdvanceAsync"/> (state-guarded
/// UPDATEs on <see cref="PostgresAccountDeletionStore"/>); this class only owns the
/// scheduling loop.
/// </summary>
public sealed class AccountDeletionPurgeWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<AccountDeletionPurgeOptions> _options;
    private readonly ILogger<AccountDeletionPurgeWorker> _logger;

    public AccountDeletionPurgeWorker(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<AccountDeletionPurgeOptions> options,
        ILogger<AccountDeletionPurgeWorker> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // F11: guard a misconfigured interval. A zero interval would hot-spin the loop;
        // a NEGATIVE interval makes Task.Delay throw ArgumentOutOfRangeException, which
        // (only OperationCanceledException is caught around the delay) would escape
        // ExecuteAsync and FAULT the BackgroundService — silently killing the sweep.
        // Clamp any non-positive value to the sane hourly default instead.
        var interval = _options.Value.SweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            _logger.LogWarning(
                "AccountDeletionPurge SweepInterval was {Interval} (must be > 0); clamping to 1h default.",
                interval);
            interval = TimeSpan.FromHours(1);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Account-deletion purge sweep failed");
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Force-runnable entry point — advances every open <c>account_deletions</c> row
    /// exactly once: <c>pending_active_delivery</c> rows whose user has no more
    /// active deliveries move to <c>scheduled</c>; <c>scheduled</c> rows whose
    /// <c>scheduled_purge_at</c> is due move to <c>completed</c> and have their PII
    /// hard-deleted. Safe to call repeatedly / concurrently — every transition in
    /// <see cref="PostgresAccountDeletionStore.AdvanceAsync"/> is state-guarded, so a
    /// redundant call is a no-op for rows already advanced.
    /// </summary>
    public async Task PurgeOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAccountDeletionStore>();

        var now = _clock.GetUtcNow();
        await store.AdvanceAsync(now, ct);

        _logger.LogInformation("Account-deletion purge sweep completed at {Now}", now);
    }
}
