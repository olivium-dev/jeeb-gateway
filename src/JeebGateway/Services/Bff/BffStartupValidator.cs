using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC1 — startup validator that fails the host with a
/// structured error when any required downstream BaseUrl is missing.
///
/// Wired as an <see cref="IHostedService"/> so the failure surfaces before
/// the first request — the orchestrator restarts a pod that crash-loops on
/// boot rather than serving 502s while operators chase a typo in env.
///
/// The error message names every missing config key (not just the first one)
/// so a single round-trip to ops resolves the misconfig.
///
/// Behaviour:
///   * <c>DownstreamServicesOptions.RequiredInProduction</c> = false → no-op.
///     Dev / Testing environments use this to start without every backend.
///   * Environment is Development / Testing → no-op regardless of the flag.
///     Same rationale: WebApplicationFactory and local dotnet run should
///     not require every URL.
///   * Otherwise → enumerate <c>Required</c>, collect every section whose
///     <c>{section}:BaseUrl</c> is missing/whitespace, and throw a single
///     <see cref="StartupConfigurationException"/> listing them.
/// </summary>
public sealed class BffStartupValidator : IHostedService
{
    private readonly IOptions<DownstreamServicesOptions> _options;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;

    public BffStartupValidator(
        IOptions<DownstreamServicesOptions> options,
        IConfiguration config,
        IHostEnvironment env)
    {
        _options = options;
        _config = config;
        _env = env;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Exposed for unit tests so they don't have to stand up an IHost just
    /// to assert the validation result. Same logic as <see cref="StartAsync"/>.
    /// </summary>
    public void Validate()
    {
        var opts = _options.Value;

        if (!opts.RequiredInProduction)
        {
            return;
        }

        if (_env.IsDevelopment()
            || string.Equals(_env.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var missing = new List<string>();
        foreach (var section in opts.Required)
        {
            var key = $"Services:{section}:BaseUrl";
            // Accept either Services:Foo:BaseUrl or Services:Foo as a bare
            // URL (see ServiceClientExtensions.BindBaseAddress for the same
            // tolerance). Both forms count as "configured".
            var nested = _config[key];
            var bare = _config[$"Services:{section}"];
            if (string.IsNullOrWhiteSpace(nested) && string.IsNullOrWhiteSpace(bare))
            {
                missing.Add(key);
            }
        }

        if (missing.Count > 0)
        {
            throw new StartupConfigurationException(
                $"jeeb-gateway boot failed — required downstream BaseUrl(s) missing in environment '{_env.EnvironmentName}': "
                + string.Join(", ", missing)
                + ". Set the listed config keys (env vars or appsettings) before starting the gateway.");
        }
    }
}

/// <summary>
/// Distinct exception type so operators / log alerts can match on the
/// boot-time misconfig signal specifically rather than a bare
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class StartupConfigurationException : Exception
{
    public StartupConfigurationException(string message) : base(message) { }
}
