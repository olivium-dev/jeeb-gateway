using Microsoft.Extensions.Configuration;
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
            // Resolve the canonical config key for this section. Most sections
            // use the nested Services:{section}:BaseUrl shape, but a few (e.g.
            // UserManagement) were migrated to a top-level
            // {Service}ServiceApi:BaseUrl key and are dialed via an NSwag client
            // registered directly in Program.cs — the validator must enforce the
            // SAME key the app actually reads (config-key-drift fix). See
            // DownstreamServicesOptions.ConfigKeyFor.
            var key = opts.ConfigKeyFor(section);
            // Accept either {key} or its bare-URL form ({key} minus a trailing
            // ":BaseUrl") — see ServiceClientExtensions.BindBaseAddress for the
            // same tolerance. Both forms count as "configured".
            var nested = _config[key];
            var bareKey = key.EndsWith(":BaseUrl", StringComparison.OrdinalIgnoreCase)
                ? key[..^":BaseUrl".Length]
                : key;
            var bare = _config[bareKey];
            if (string.IsNullOrWhiteSpace(nested) && string.IsNullOrWhiteSpace(bare))
            {
                missing.Add(key);
            }
        }

        // Collect every distinct boot-time misconfig so a single round-trip to
        // ops resolves all of them (same rationale as the missing-key list).
        var problems = new List<string>();

        if (missing.Count > 0)
        {
            problems.Add(
                $"required downstream BaseUrl(s) missing in environment '{_env.EnvironmentName}': "
                + string.Join(", ", missing)
                + ". Set the listed config keys (env vars or appsettings) before starting the gateway.");
        }

        // JEBV4 OTP-502 guard — the phone-OTP login outage of 2026-07-12.
        //
        // AuthOtpController forwards Auth:Otp:ApplicationId (the Jeeb tenant's
        // registered application id) as `applicationId` on every SendOTP /
        // ValidateOTP to the shared one-time-password service, which REQUIRES a
        // non-empty, registered id and 400s otherwise. The committed value is an
        // intentionally-empty placeholder injected at deploy via the env var
        // Auth__Otp__ApplicationId. If OTP is enabled (FeatureFlags:UseUpstream:Otp)
        // but that injection is missing, the gateway silently POSTs an empty
        // applicationId → upstream 400 → gateway 502 → EVERY phone-OTP login fails
        // on ALL devices. Fail the boot LOUDLY here instead of serving 502s while
        // operators chase the outage (the orchestrator restarts / surfaces a
        // crash-loop; a live 502-storm is invisible to it).
        var otpEnabled = _config.GetValue<bool>("FeatureFlags:UseUpstream:Otp");
        if (otpEnabled && string.IsNullOrWhiteSpace(_config["Auth:Otp:ApplicationId"]))
        {
            problems.Add(
                "Auth:Otp:ApplicationId is empty while OTP sign-in is enabled "
                + "(FeatureFlags:UseUpstream:Otp=true). The gateway would POST an empty applicationId "
                + "to the one-time-password service and every /v1/auth/otp/* call would 502 — a total "
                + "phone-OTP login outage. Set env Auth__Otp__ApplicationId to the Jeeb tenant's "
                + "registered one-time-password application id before starting the gateway.");
        }

        if (problems.Count > 0)
        {
            throw new StartupConfigurationException(
                "jeeb-gateway boot failed — " + string.Join(" | ", problems));
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
