// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — Ported from updated-requirements/qa-scaffolding/JEB-467/
//   auth-service/AuthService.Tests/Otp/Fixtures/OtpServiceWebAppFactory.cs
//
// Port adjustments:
//   - Namespace: AuthService.Tests.Otp.Fixtures → JeebGateway.IntegrationTests.OtpSignIn.Fixtures
//   - WebApplicationFactory<Program> targets the production
//     JeebGateway.Program (the existing partial-class marker at the end of
//     src/JeebGateway/Program.cs makes it visible).
//   - Replaces production IServiceOtpClient and
//     IUserManagementPhoneIdentityClient (not the scaffolding's marker
//     "IFake*" types).
//   - Disables global rate-limit middleware via SecurityOptions config so
//     it does not collide with the per-controller IOtpRequestRateLimiter
//     that the OTP-specific tests assert against.
//   - Drops the Testcontainers Postgres dep — refresh tokens live in an
//     in-memory store on the gateway; the QA-POST suite (JEB-469) will
//     bring a real Postgres if needed.

using System.Diagnostics;
using System.Net.Http;
using JeebGateway.Auth.OtpSignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace JeebGateway.IntegrationTests.OtpSignIn.Fixtures;

public sealed class OtpServiceWebAppFactory : WebApplicationFactory<Program>
{
    public FakeTimeProvider          Clock          { get; } = new();
    public FakeOneTimePasswordClient OtpClient      { get; private set; } = null!;
    public FakeUserManagementClient  UserMgmtClient { get; } = new();
    public CapturingLoggerProvider   LogCapture     { get; } = new();
    public InMemorySpanExporter      SpanExporter   { get; } = new();

    // Monotonically increasing epoch per ResetState() — FakeTimeProvider
    // refuses to move time backwards, so each test gets a fresh "day"
    // window forward from the test epoch instead of resetting to a fixed point.
    private static readonly DateTimeOffset BaseEpoch =
        new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
    private int _resetCount;

    public OtpServiceWebAppFactory()
    {
        // Seed the clock immediately so any service that captures
        // TimeProvider.GetUtcNow at construction sees the test epoch.
        Clock.SetUtcNow(BaseEpoch);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Wire log capture BEFORE Program.cs sets up its own logging.
        builder.ConfigureLogging(b => b.AddProvider(LogCapture));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // JeebJwt — HS512 signing key (env-only in prod, in-memory here).
                // HS512 requires ≥ 64-byte key (512 bits). This 80-byte test
                // string satisfies both the data-annotation MinLength(64) and
                // the IdentityModel runtime ValidateKeySize check.
                ["JeebJwt:SigningKey"]      = "test-signing-key-must-be-at-least-sixty-four-bytes-for-HS512-padding!!",
                ["JeebJwt:Issuer"]          = "https://test.auth.jeeb",
                ["JeebJwt:Audience"]        = "jeeb-mobile",
                ["JeebJwt:AccessTtlSeconds"]  = "3600",
                ["JeebJwt:RefreshTtlSeconds"] = "2592000",

                // GatewayRateLimit — audit #14764.
                ["GatewayRateLimit:PerPhonePerMin"] = "3",
                ["GatewayRateLimit:PerIpPerMin"]    = "10",

                // Service base URLs — irrelevant once we substitute typed clients.
                ["UserManagementApi:BaseUrl"] = "http://fake-user-mgmt",
                ["ServiceOTPApi:BaseUrl"]     = "http://fake-otp",

                // Disable the OLD edge-rate-limiter middleware (T-backend-032) so
                // the per-controller IOtpRequestRateLimiter is the only limiter
                // exercised by these tests. The OLD limiter has its own coverage
                // in NamedRateLimitPolicyTests.
                ["Security:RateLimit:Enabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace TimeProvider with FakeTimeProvider so TTL tests can
            // advance time deterministically. Production registration uses
            // TryAdd, so we explicitly RemoveAll first.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // Replace the OTP downstream client (NO default registration in
            // production; tests must wire one in).
            services.RemoveAll<IServiceOtpClient>();
            OtpClient = new FakeOneTimePasswordClient(Clock);
            services.AddSingleton<IServiceOtpClient>(OtpClient);

            // Replace the user-management phone-identity client (production
            // default is the fail-closed NotConfigured shim).
            services.RemoveAll<IUserManagementPhoneIdentityClient>();
            services.AddSingleton<IUserManagementPhoneIdentityClient>(UserMgmtClient);

            // OTel in-memory exporter for span attribute assertions
            // (AC-PhonePIIHash bcrypt-presence + raw-phone scrub).
            services
                .AddOpenTelemetry()
                .WithTracing(t => t
                    .AddSource(OtpSignInActivitySource.Name)
                    .AddProcessor(new SimpleActivityExportProcessor(SpanExporter)));
        });
    }

    /// <summary>Resets per-test mutable state. Each invocation advances the
    /// fake clock by 1 day to avoid FakeTimeProvider's "cannot go back in time"
    /// constraint while preserving per-test isolation of the time-based
    /// assertions (TTL, refresh exp, rate-limit window).</summary>
    public void ResetState()
    {
        OtpClient.Reset();
        UserMgmtClient.Reset();
        LogCapture.Records.Clear();
        SpanExporter.Spans.Clear();

        var target = BaseEpoch.AddDays(System.Threading.Interlocked.Increment(ref _resetCount));
        var current = Clock.GetUtcNow();
        if (target > current)
        {
            Clock.SetUtcNow(target);
        }
    }

    public HttpClient CreateAuthClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress       = new Uri("http://localhost"),
    });
}

/// <summary>
/// Captures every log record so the PII scrubber test can scan scopes,
/// message templates, and structured properties for raw phone substrings.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentBag<LogRecord> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Records);
    public void Dispose() { }

    public sealed record LogRecord(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        string? Message,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        IReadOnlyList<object> Scopes);

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly System.Collections.Concurrent.ConcurrentBag<LogRecord> _sink;
        private readonly System.Threading.AsyncLocal<List<object>> _scopes = new();

        public CapturingLogger(string category, System.Collections.Concurrent.ConcurrentBag<LogRecord> sink)
        {
            _category = category;
            _sink     = sink;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _scopes.Value ??= new();
            _scopes.Value.Add(state!);
            return new Pop(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var stateKv = state is IReadOnlyList<KeyValuePair<string, object?>> kv
                ? kv
                : (IReadOnlyList<KeyValuePair<string, object?>>)Array.Empty<KeyValuePair<string, object?>>();
            var scopes  = _scopes.Value?.ToArray() ?? Array.Empty<object>();
            _sink.Add(new LogRecord(_category, logLevel, eventId, formatter(state, exception), stateKv, scopes));
        }

        private sealed class Pop : IDisposable
        {
            private readonly System.Threading.AsyncLocal<List<object>> _scopes;
            public Pop(System.Threading.AsyncLocal<List<object>> scopes) => _scopes = scopes;
            public void Dispose()
            {
                if (_scopes.Value is { Count: > 0 } l) l.RemoveAt(l.Count - 1);
            }
        }
    }
}

/// <summary>
/// Minimal OTel in-memory exporter — collects every <see cref="System.Diagnostics.Activity"/>
/// finished while the tracer provider is alive so AC-PhonePIIHash and AC6
/// assertions can scan span tags.
/// </summary>
public sealed class InMemorySpanExporter : BaseExporter<Activity>
{
    public List<Activity> Spans { get; } = new();

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            Spans.Add(activity);
        }
        return ExportResult.Success;
    }
}
