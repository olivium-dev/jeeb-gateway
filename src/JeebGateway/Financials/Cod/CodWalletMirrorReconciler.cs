using JeebGateway.Migration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials.Cod;

// gwdbx W2-05 — durable COD dual-write: sweeps settled rows whose wallet_tx_id is NULL and mirrors
// them to wallet-service, stamping the returned earning id. Fully inert below dual-write-local-read.
public sealed class CodWalletMirrorReconciler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly IOptionsMonitor<CodWalletMirrorOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CodWalletMirrorReconciler> _log;

    public CodWalletMirrorReconciler(
        IServiceProvider services,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        IOptionsMonitor<CodWalletMirrorOptions> options,
        TimeProvider clock,
        ILogger<CodWalletMirrorReconciler> log)
    {
        _services = services;
        _mode = mode;
        _options = options;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _log.LogError(ex, "cod wallet mirror sweep failed");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.SweepIntervalSeconds)),
                    _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // One pass; public for tests. Returns rows mirrored-and-stamped (dry-run always returns 0).
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (_mode.CurrentValue.CodSettlement < GwdbxMigrationPhase.DualWriteLocalRead)
        {
            return 0;
        }

        var options = _options.CurrentValue;
        if (!options.TryParseReplayFrom(out var replayFrom))
        {
            // ValidateOnStart already refuses this boot; belt-and-braces for tests driving directly.
            _log.LogError("cod wallet mirror: CodWalletMirror:ReplayFromUtc missing/unparseable; sweep skipped");
            return 0;
        }

        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettlementStore>();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletApiSettlementLedgerClient>();

        var rows = await store.ListWalletUnmirroredAsync(
            replayFrom, Math.Max(1, options.PageSize), ct);
        if (rows.Count == 0)
        {
            return 0;
        }

        var mirrored = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (options.DryRun)
            {
                _log.LogInformation(
                    "cod wallet mirror DRY-RUN would post settlement {SettlementId} delivery "
                    + "{DeliveryId} jeeber {JeeberId} gross {Gross} commission {Commission}",
                    row.Id, row.DeliveryId, row.JeeberId, row.GoodsCost, row.Commission);
                continue;
            }

            try
            {
                var walletTxId = await wallet.MirrorAsync(row, ct);
                if (walletTxId is null)
                {
                    continue;
                }

                await store.SetWalletTxIdAsync(row.Id, walletTxId, ct);
                mirrored++;
                _log.LogInformation(
                    "cod wallet mirror stamped settlement {SettlementId} -> wallet earning {WalletTxId}",
                    row.Id, walletTxId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-row isolation: one failing row (wallet down, bad row) never wedges the sweep.
                _log.LogWarning(ex,
                    "cod wallet mirror post failed for settlement {SettlementId}; will retry next sweep",
                    row.Id);
            }
        }

        return mirrored;
    }
}
