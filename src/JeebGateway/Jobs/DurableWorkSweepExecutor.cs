using System.Text.Json;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.Options;

namespace JeebGateway.Jobs;

public sealed class DurableWorkExecutionOptions
{
    public const string SectionName = "DurableWorkExecution";

    public int MaxBatchSize { get; init; } = 20;
    public int MaxConcurrency { get; init; } = 4;
    public int LeaseSeconds { get; init; } = 120;
    public TimeSpan LeaseRenewInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan UnhandledRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public string? WorkerId { get; init; }
}

public interface IDurableWorkItemHandler
{
    string Kind { get; }
    Task<DurableWorkExecutionResult> ExecuteAsync(StateWorkItem item, CancellationToken ct);
}

public enum DurableWorkOutcome
{
    Complete,
    Defer,
    Retry,
    Fail
}

public sealed record DurableWorkExecutionResult(
    DurableWorkOutcome Outcome,
    JsonElement? Result,
    string? Error,
    DateTimeOffset? RetryAt,
    string? ArtifactRef,
    DateTimeOffset? ArtifactExpiresAt,
    string? DownloadTokenHash)
{
    public static DurableWorkExecutionResult Completed(
        JsonElement? result = null,
        string? artifactRef = null,
        DateTimeOffset? artifactExpiresAt = null,
        string? downloadTokenHash = null) =>
        new(DurableWorkOutcome.Complete, result, null, null,
            artifactRef, artifactExpiresAt, downloadTokenHash);

    public static DurableWorkExecutionResult Retry(string error, DateTimeOffset retryAt) =>
        new(DurableWorkOutcome.Retry, null, error, retryAt, null, null, null);

    public static DurableWorkExecutionResult Deferred(string reason, DateTimeOffset dueAt) =>
        new(DurableWorkOutcome.Defer, null, reason, dueAt, null, null, null);

    public static DurableWorkExecutionResult Failed(string error) =>
        new(DurableWorkOutcome.Fail, null, error, null, null, null, null);
}

public sealed record DurableSweepSummary(
    string Kind,
    int Claimed,
    int Completed,
    int Deferred,
    int Retried,
    int Failed,
    int LeaseLost,
    int Errors);

/// <summary>
/// Executes one externally scheduled bounded sweep. State-service atomically
/// claims rows across replicas; each side-effect wave retains its lease through
/// CAS renewals before completing or failing that exact version.
/// </summary>
public sealed class DurableWorkSweepExecutor(
    IStateWorkItemClient work,
    IEnumerable<IDurableWorkItemHandler> handlers,
    IOptions<DurableWorkExecutionOptions> options,
    TimeProvider clock,
    ILogger<DurableWorkSweepExecutor> logger)
{
    private readonly IReadOnlyDictionary<string, IDurableWorkItemHandler> _handlers =
        handlers.ToDictionary(handler => handler.Kind, StringComparer.Ordinal);
    private readonly string _workerId = string.IsNullOrWhiteSpace(options.Value.WorkerId)
        ? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"
        : options.Value.WorkerId.Trim();

    public async Task<DurableSweepSummary> SweepAsync(
        string kind,
        int? requestedLimit,
        CancellationToken ct)
    {
        if (!_handlers.TryGetValue(kind, out var handler))
            throw new InvalidOperationException($"No durable work handler is registered for kind '{kind}'.");

        var configured = options.Value;
        var maxBatch = Math.Clamp(configured.MaxBatchSize, 1, 100);
        var limit = Math.Clamp(requestedLimit ?? maxBatch, 1, maxBatch);
        var leaseSeconds = Math.Clamp(configured.LeaseSeconds, 10, 3600);
        ValidateRenewal(configured.LeaseRenewInterval, leaseSeconds);

        var claimed = await work.ClaimAsync(
            new StateWorkClaim(
                DurableWorkContract.Application,
                [kind],
                _workerId,
                leaseSeconds,
                limit),
            ct);
        if (claimed.Count == 0)
            return new DurableSweepSummary(kind, 0, 0, 0, 0, 0, 0, 0);

        var outcomes = new SweepItemOutcome[claimed.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, claimed.Count),
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Clamp(configured.MaxConcurrency, 1, maxBatch)
            },
            async (index, itemCt) =>
            {
                outcomes[index] = await ExecuteClaimAsync(
                    claimed[index], handler, leaseSeconds, configured, itemCt);
            });

        return new DurableSweepSummary(
            kind,
            claimed.Count,
            outcomes.Count(value => value == SweepItemOutcome.Completed),
            outcomes.Count(value => value == SweepItemOutcome.Deferred),
            outcomes.Count(value => value == SweepItemOutcome.Retried),
            outcomes.Count(value => value == SweepItemOutcome.Failed),
            outcomes.Count(value => value == SweepItemOutcome.LeaseLost),
            outcomes.Count(value => value == SweepItemOutcome.Error));
    }

    private async Task<SweepItemOutcome> ExecuteClaimAsync(
        StateWorkItem item,
        IDurableWorkItemHandler handler,
        int leaseSeconds,
        DurableWorkExecutionOptions configured,
        CancellationToken sweepCt)
    {
        if (item.LeaseToken is not { } leaseToken)
        {
            logger.LogError("State-service returned unleased work item {WorkItemId}", item.WorkItemId);
            return SweepItemOutcome.Error;
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(sweepCt);
        using var stopRenewal = CancellationTokenSource.CreateLinkedTokenSource(sweepCt);
        var version = item.Version;
        Exception? renewalFailure = null;
        var renewal = RenewLoopAsync();

        DurableWorkExecutionResult result;
        try
        {
            result = await handler.ExecuteAsync(item, execution.Token);
        }
        catch (OperationCanceledException) when (sweepCt.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (renewalFailure is not null)
        {
            result = DurableWorkExecutionResult.Failed("lease lost while executing work");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Durable work {WorkItemId} ({Kind}) orchestration failed", item.WorkItemId, item.Kind);
            var retryAt = clock.GetUtcNow() + PositiveOrDefault(
                configured.UnhandledRetryDelay,
                TimeSpan.FromMinutes(5));
            result = item.Attempts >= item.MaxAttempts
                ? DurableWorkExecutionResult.Failed(BoundedError(ex.Message))
                : DurableWorkExecutionResult.Retry(BoundedError(ex.Message), retryAt);
        }
        finally
        {
            stopRenewal.Cancel();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException) when (stopRenewal.IsCancellationRequested)
            {
                // Normal handoff from renewal to the terminal CAS.
            }
        }

        if (renewalFailure is not null)
        {
            logger.LogWarning(renewalFailure,
                "Lease lost for durable work {WorkItemId}; terminal mutation suppressed",
                item.WorkItemId);
            return SweepItemOutcome.LeaseLost;
        }

        try
        {
            switch (result.Outcome)
            {
                case DurableWorkOutcome.Complete:
                    await work.CompleteAsync(
                        item.WorkItemId,
                        new StateWorkComplete(
                            leaseToken,
                            version,
                            result.Result,
                            result.ArtifactRef,
                            result.ArtifactExpiresAt,
                            result.DownloadTokenHash),
                        sweepCt);
                    return SweepItemOutcome.Completed;

                case DurableWorkOutcome.Defer:
                    await work.DeferAsync(
                        item.WorkItemId,
                        new StateWorkDefer(
                            leaseToken,
                            version,
                            result.RetryAt ?? clock.GetUtcNow() + TimeSpan.FromMinutes(5),
                            BoundedError(result.Error)),
                        sweepCt);
                    return SweepItemOutcome.Deferred;

                case DurableWorkOutcome.Retry:
                    await work.FailAsync(
                        item.WorkItemId,
                        new StateWorkFail(
                            leaseToken,
                            version,
                            BoundedError(result.Error),
                            result.RetryAt ?? clock.GetUtcNow() + TimeSpan.FromMinutes(5)),
                        sweepCt);
                    return SweepItemOutcome.Retried;

                default:
                    await work.FailAsync(
                        item.WorkItemId,
                        new StateWorkFail(
                            leaseToken,
                            version,
                            BoundedError(result.Error),
                            RetryAt: null),
                        sweepCt);
                    return SweepItemOutcome.Failed;
            }
        }
        catch (StateWorkConflictException ex)
        {
            logger.LogWarning(ex, "Terminal CAS lost for durable work {WorkItemId}", item.WorkItemId);
            return SweepItemOutcome.LeaseLost;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Terminal state mutation failed for durable work {WorkItemId}", item.WorkItemId);
            return SweepItemOutcome.Error;
        }

        async Task RenewLoopAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(configured.LeaseRenewInterval, clock, stopRenewal.Token);
                    var renewed = await work.RenewLeaseAsync(
                        item.WorkItemId,
                        new StateWorkLeaseRenew(leaseToken, version, leaseSeconds),
                        stopRenewal.Token);
                    version = renewed.Version;
                }
            }
            catch (OperationCanceledException) when (stopRenewal.IsCancellationRequested)
            {
                // Normal stop before terminal CAS.
            }
            catch (Exception ex)
            {
                renewalFailure = ex;
                execution.Cancel();
            }
        }
    }

    private static void ValidateRenewal(TimeSpan interval, int leaseSeconds)
    {
        if (interval <= TimeSpan.Zero || interval >= TimeSpan.FromSeconds(leaseSeconds))
            throw new InvalidOperationException(
                "DurableWorkExecution:LeaseRenewInterval must be positive and shorter than LeaseSeconds.");
    }

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;

    private static string BoundedError(string? error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "durable work failed" : error.Trim();
        return value.Length <= 4000 ? value : value[..4000];
    }

    private enum SweepItemOutcome
    {
        Completed,
        Deferred,
        Retried,
        Failed,
        LeaseLost,
        Error
    }
}
