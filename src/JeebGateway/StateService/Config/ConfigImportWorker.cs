using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.StateService.Config;

// gwdbx W3-07 PREP — one-shot runner for the W3-03 freeze-import + parity check. Ships INERT:
// Enabled defaults false, and armed it dry-runs (parity only, zero writes) by default.
public sealed class ConfigImportRunOptions
{
    public const string SectionName = "ConfigImportRun";

    /// <summary>False (default) = the worker touches neither the local stores nor upstream.</summary>
    public bool Enabled { get; init; }

    /// <summary>True (default) = parity check only, no upstream write. False = import, then parity.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Passed to the importer: re-import even when a mode already serves upstream reads.</summary>
    public bool Force { get; init; }
}

/// <summary>Runs the W3-03 importer and/or the parity checker once at boot when armed.</summary>
public sealed class ConfigImportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<ConfigImportRunOptions> _options;
    private readonly ILogger<ConfigImportWorker> _log;

    public ConfigImportWorker(
        IServiceScopeFactory scopes,
        IOptions<ConfigImportRunOptions> options,
        ILogger<ConfigImportWorker> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    /// <summary>Last completed runs, for post-run assertion by tests and the W3-07 runbook.</summary>
    public ConfigImportReport? LastImportReport { get; private set; }

    public ConfigParityReport? LastParityReport { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _log.LogInformation(
                "config import/parity (W3-07 prep) is DISARMED ({Section}:Enabled=false): no store read, no upstream call.",
                ConfigImportRunOptions.SectionName);
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            if (!options.DryRun)
            {
                LastImportReport = await scope.ServiceProvider
                    .GetRequiredService<StateServiceConfigImporter>()
                    .ImportAsync(options.Force, stoppingToken);
            }

            var parity = await scope.ServiceProvider
                .GetRequiredService<ConfigParityChecker>()
                .CheckAsync(stoppingToken);
            LastParityReport = parity;

            if (!parity.Clean)
            {
                _log.LogWarning(
                    "config parity (W3-07 prep) NOT clean: {Count} mismatches{Truncated}: {Mismatches}",
                    parity.Mismatches.Count, parity.Truncated ? " (truncated)" : string.Empty,
                    string.Join(" | ", parity.Mismatches));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _log.LogWarning("config import/parity (W3-07 prep) cancelled by shutdown; re-arm and re-run — it is idempotent.");
        }
        catch (Exception ex)
        {
            // A prep run that cannot finish must not take the gateway down with it.
            _log.LogError(ex, "config import/parity (W3-07 prep) aborted; NOTHING is proven imported or matched.");
        }
    }
}
