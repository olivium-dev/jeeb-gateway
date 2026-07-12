using JeebGateway.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

/// <summary>
/// JEBV4-47 (M3/R7): the background reconciler the misleading "will replay" comment
/// in <see cref="SettlementService"/> used to promise but that did not exist.
///
/// <para>When the UPG generic-settlement ledger post fails at settle time, the
/// settlement row is persisted with <c>ledger_entry_id</c> NULL (the gateway is the
/// system of record) and the failure is counted
/// (<see cref="BusinessOutcomeTelemetry.SettlementLedgerPostFailures"/>). Left alone
/// the gateway settlement rows and the UPG ledger diverge forever. This hosted
/// service periodically re-posts those unposted rows and stamps
/// <c>ledger_entry_id</c> on success so the two reconverge.</para>
///
/// <para><b>Idempotent + bounded.</b> The re-post reuses the settlement id as the
/// <see cref="LedgerEntryRequest.IdempotencyKey"/> (exactly as the settle path does),
/// so a row that actually posted upstream but whose stamp was lost returns the
/// existing entry — never a double credit. Each sweep pulls a bounded page
/// (<see cref="SettlementLedgerReconcilerOptions.PageSize"/>) with a stable
/// <c>ORDER BY settled_at, id</c>, and every row is isolated in its own try/catch so
/// one still-failing row (UPG still down) cannot wedge the sweep.</para>
/// </summary>
public sealed class SettlementLedgerReconciler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<SettlementLedgerReconcilerOptions> _options;
    private readonly ILogger<SettlementLedgerReconciler> _logger;

    public SettlementLedgerReconciler(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<SettlementLedgerReconcilerOptions> options,
        ILogger<SettlementLedgerReconciler> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.SweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Settlement ledger reconcile sweep failed");
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
    /// One reconcile pass. Re-posts every unposted settlement ledger row on the
    /// bounded page and stamps <c>ledger_entry_id</c> on success. Returns the number
    /// of rows successfully reconciled (for tests / observability).
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettlementStore>();
        var ledger = scope.ServiceProvider.GetRequiredService<ISettlementLedgerClient>();

        var unposted = await store.ListUnpostedLedgerAsync(_options.Value.PageSize, ct);
        if (unposted.Count == 0)
        {
            return 0;
        }

        var reconciled = 0;
        foreach (var row in unposted)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = await ledger.PostLedgerEntryAsync(new LedgerEntryRequest
                {
                    DeliveryId = row.DeliveryId,
                    JeeberId = row.JeeberId,
                    ClientId = row.ClientId,
                    EntryType = "cash_settlement",
                    GoodsCost = row.GoodsCost,
                    Commission = row.Commission,
                    Insurance = row.Insurance,
                    Total = row.Total,
                    Currency = row.Currency,
                    PaymentMethod = row.PaymentMethod,
                    // Same key the settle path used — a row that DID post upstream but
                    // lost its stamp returns the existing entry (no double credit).
                    IdempotencyKey = row.Id,
                }, ct);

                await store.SetLedgerEntryAsync(row.Id, entry.LedgerEntryId, ct);
                BusinessOutcomeTelemetry.SettlementLedgerReconciled.Add(1);
                reconciled++;
                _logger.LogInformation(
                    "Settlement {SettlementId} (delivery {DeliveryId}) ledger post replayed as {LedgerEntryId}.",
                    row.Id, row.DeliveryId, entry.LedgerEntryId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-row isolation: UPG may still be down for this row. Leave it
                // unposted and try again next tick — never wedge the whole sweep.
                _logger.LogWarning(ex,
                    "Settlement {SettlementId} ledger replay still failing; leaving unposted for the next sweep.",
                    row.Id);
            }
        }

        return reconciled;
    }
}
