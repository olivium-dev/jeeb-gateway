#nullable enable

using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JeebGateway.StateService.Work;

/// <summary>One work-item kind the gateway claims and executes; throwing fails the item.</summary>
public interface IWorkItemExecutor
{
    /// <summary>The state-service work-item kind (application=jeeb-gateway) this executor owns.</summary>
    string Kind { get; }

    /// <summary>Mode gate. False means the kind is never claimed — every executor ships false.</summary>
    bool Enabled { get; }

    /// <summary>Returning completes the item; throwing fails it under the same lease.</summary>
    Task ExecuteAsync(WorkItemRecordV1 item, CancellationToken ct);
}

/// <summary>
/// The claimer W1-09 shipped without: complete/fail need a lease and the ONLY lease source is
/// the batch POST /v1/work-items/claim, so an inline producer can never terminate its own item.
/// </summary>
public sealed class WorkItemClaimWorker : BackgroundService
{
    public const string Application = "jeeb-gateway";

    private const int LeaseSeconds = 120;
    private const int ClaimLimit = 50;
    private const int MaxRecordedErrorLength = 500;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkItemClaimWorker> _log;
    private readonly string _workerId = "jeeb-gateway-" + Environment.MachineName;

    public WorkItemClaimWorker(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<WorkItemClaimWorker> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AnyKindEnabled())
        {
            // Ships INERT: with every mode key at its default nothing is claimable, so the
            // loop never starts and the gateway issues zero claim calls.
            _log.LogInformation("Work-item claim worker idle: no work-item kind is enabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Work-item claim sweep failed; retrying after the idle delay.");
            }

            if (claimed >= ClaimLimit)
            {
                continue;
            }

            try
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One claim-execute-settle sweep; returns how many items were claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var executors = scope.ServiceProvider.GetServices<IWorkItemExecutor>()
            .Where(executor => executor.Enabled)
            .ToDictionary(executor => executor.Kind, StringComparer.Ordinal);
        if (executors.Count == 0)
        {
            return 0;
        }

        var client = scope.ServiceProvider.GetRequiredService<IStateOwnershipClient>();
        var claimed = await client.ClaimWorkItemsAsync(
            new WorkClaimRequestV1
            {
                Application = Application,
                Kinds = executors.Keys.ToArray(),
                WorkerId = _workerId,
                LeaseSeconds = LeaseSeconds,
                Limit = ClaimLimit,
            },
            ct);

        foreach (var item in claimed)
        {
            try
            {
                await SettleAsync(client, executors, item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One poisoned item must not abandon the leases of the rest of the batch.
                _log.LogWarning(ex, "Settling work item {WorkItemId} failed.", item.WorkItemId);
            }
        }

        return claimed.Count;
    }

    private async Task SettleAsync(
        IStateOwnershipClient client,
        IReadOnlyDictionary<string, IWorkItemExecutor> executors,
        WorkItemRecordV1 item,
        CancellationToken ct)
    {
        if (item.LeaseToken is not { } lease || !executors.TryGetValue(item.Kind, out var executor))
        {
            // Never CAS-guess a terminal call we hold no lease for; the lease just expires.
            _log.LogWarning(
                "Skipping work item {WorkItemId} kind={Kind}: no lease token, or no enabled executor.",
                item.WorkItemId, item.Kind);
            return;
        }

        try
        {
            await executor.ExecuteAsync(item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Work item {WorkItemId} kind={Kind} failed.", item.WorkItemId, item.Kind);
            await FailAsync(client, item, lease, ex, ct);
            return;
        }

        await client.CompleteWorkItemAsync(
            item.WorkItemId,
            new WorkCompleteRequestV1 { LeaseToken = lease, ExpectedVersion = item.Version },
            ct);
    }

    private async Task FailAsync(
        IStateOwnershipClient client,
        WorkItemRecordV1 item,
        Guid lease,
        Exception ex,
        CancellationToken ct)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}";
        if (error.Length > MaxRecordedErrorLength)
        {
            error = error[..MaxRecordedErrorLength];
        }

        try
        {
            // A non-future retryAt is a 400; state-service still fails the item outright once
            // attempts >= maxAttempts, so it owns the DLQ transition either way.
            await client.FailWorkItemAsync(
                item.WorkItemId,
                new WorkFailRequestV1
                {
                    LeaseToken = lease,
                    ExpectedVersion = item.Version,
                    Error = error,
                    RetryAt = _clock.GetUtcNow().Add(RetryDelay),
                },
                ct);
        }
        catch (Exception bookkeeping)
        {
            _log.LogWarning(bookkeeping, "Could not fail work item {WorkItemId}.", item.WorkItemId);
        }
    }

    private bool AnyKindEnabled()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            return scope.ServiceProvider.GetServices<IWorkItemExecutor>().Any(e => e.Enabled);
        }
        catch (Exception ex)
        {
            // Fail-closed to idle: an unreadable roster must never take the host down at boot.
            _log.LogWarning(ex, "Work-item executor roster unreadable; the claim worker stays idle.");
            return false;
        }
    }
}
