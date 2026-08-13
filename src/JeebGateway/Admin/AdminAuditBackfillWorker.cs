using JeebGateway.Cases;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Admin;

// gwdbx W1-04 — one-shot, idempotent relay of every admin_actions row to state-service
// /v1/audit-events. Ships INERT: Enabled defaults false, and armed it dry-runs by default.
public sealed class AdminAuditBackfillOptions
{
    public const string SectionName = "AdminAuditBackfill";

    /// <summary>False (default) = the worker returns without touching the database.</summary>
    public bool Enabled { get; init; }

    /// <summary>True (default) = enumerate and report the plan, POST nothing.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Keyset page size for the admin_actions scan.</summary>
    public int BatchSize { get; init; } = 200;
}

/// <summary>Counts from one relay pass; every scanned row lands in exactly one bucket.</summary>
/// <remarks>Accepted covers 201-created and 200-replayed alike: the typed client returns the
/// record for both, and the only thing this backfill asserts is "present upstream".</remarks>
public sealed record AdminAuditBackfillReport(
    long LocalRows, int Scanned, int Accepted, int Conflicted, int Failed, bool DryRun)
{
    public bool Complete => Failed == 0 && Scanned == LocalRows;
}

/// <summary>
/// Replays admin_actions upstream under G-15 keys (Idempotency-Key = admin_actions.id), so a
/// row the live mirror already wrote replays as 200 rather than duplicating.
/// </summary>
public sealed class AdminAuditBackfillWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<AdminAuditBackfillOptions> _options;
    private readonly ILogger<AdminAuditBackfillWorker> _log;

    public AdminAuditBackfillWorker(
        IServiceScopeFactory scopes,
        IOptions<AdminAuditBackfillOptions> options,
        ILogger<AdminAuditBackfillWorker> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    /// <summary>The last completed pass, for post-run assertion by tests and the runbook.</summary>
    public AdminAuditBackfillReport? LastReport { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _log.LogInformation(
                "admin-audit backfill (W1-04) is DISARMED ({Section}:Enabled=false): no admin_actions read, no upstream call.",
                AdminAuditBackfillOptions.SectionName);
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var report = await RunOnceAsync(
                scope.ServiceProvider.GetRequiredService<IAdminAuditBackfillSource>(),
                scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>(),
                options,
                stoppingToken);
            LastReport = report;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _log.LogWarning("admin-audit backfill (W1-04) cancelled by shutdown; re-arm and re-run — it is idempotent.");
        }
        catch (Exception ex)
        {
            // A backfill that cannot run must not take the gateway down with it.
            _log.LogError(ex, "admin-audit backfill (W1-04) aborted; NOTHING is proven relayed. Re-arm and re-run.");
        }
    }

    public async Task<AdminAuditBackfillReport> RunOnceAsync(
        IAdminAuditBackfillSource source,
        IStateOwnershipClient upstream,
        AdminAuditBackfillOptions options,
        CancellationToken ct)
    {
        var local = await source.CountAsync(ct);
        _log.LogInformation(
            "admin-audit backfill (W1-04) START dryRun={DryRun} localRows={LocalRows} batchSize={BatchSize}.",
            options.DryRun, local, options.BatchSize);

        var limit = Math.Clamp(options.BatchSize, 1, 1000);
        int scanned = 0, accepted = 0, conflicted = 0, failed = 0;
        AdminAuditCursor? cursor = null;

        while (!ct.IsCancellationRequested)
        {
            var page = await source.ReadPageAsync(cursor, limit, ct);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                scanned++;
                if (!Guid.TryParse(row.Id, out var rowId))
                {
                    // Cannot advance the keyset past a row whose PK is unreadable.
                    failed++;
                    _log.LogError("admin-audit backfill: admin_actions row has a non-GUID id '{Id}'; skipped.", row.Id);
                    continue;
                }

                cursor = new AdminAuditCursor(row.CreatedAt, rowId);

                if (options.DryRun)
                {
                    _log.LogInformation(
                        "admin-audit backfill DRY-RUN would relay id={Id} action={Action} entityType={EntityType} occurredAt={OccurredAt}.",
                        row.Id, row.Action, row.EntityType, row.CreatedAt);
                    continue;
                }

                switch (await RelayAsync(upstream, row, ct))
                {
                    case RelayOutcome.Accepted: accepted++; break;
                    case RelayOutcome.Conflicted: conflicted++; break;
                    default: failed++; break;
                }
            }

            if (page.Count < limit) break;
        }

        var report = new AdminAuditBackfillReport(local, scanned, accepted, conflicted, failed, options.DryRun);
        _log.LogInformation(
            "admin-audit backfill (W1-04) DONE dryRun={DryRun} localRows={LocalRows} scanned={Scanned} " +
            "accepted={Accepted} conflicted={Conflicted} failed={Failed} complete={Complete}.",
            report.DryRun, report.LocalRows, report.Scanned,
            report.Accepted, report.Conflicted, report.Failed, report.Complete);
        return report;
    }

    private enum RelayOutcome { Accepted, Conflicted, Failed }

    private async Task<RelayOutcome> RelayAsync(
        IStateOwnershipClient upstream, AdminAuditEntry row, CancellationToken ct)
    {
        try
        {
            // G-15 — the idempotency key IS admin_actions.id, the same key the mirror uses.
            await upstream.AppendAuditEventAsync(AdminAuditEventMapping.ToAppendRequest(row), row.Id, ct);
            return RelayOutcome.Accepted;
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            // The key exists upstream with a different body — the live mirror already wrote it
            // (it can carry a non-GUID resourceRef this read cannot). Present, so not a gap.
            _log.LogInformation(
                "admin-audit backfill: id={Id} already upstream under a different body (409); the mirror's row stands.",
                row.Id);
            return RelayOutcome.Conflicted;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "admin-audit backfill: relay FAILED for admin_actions.id={Id}; re-run replays it.", row.Id);
            return RelayOutcome.Failed;
        }
    }
}
