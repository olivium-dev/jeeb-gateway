using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Observability;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// GW12-OBS-6 (JEBV4-59): the business-outcome counters fire at their real
/// gateway decision points and are registered under the expected instrument
/// names.
///
/// Verification uses a <see cref="MeterListener"/> — the metrics analogue of the
/// ActivityListener the dispute-case tests use — because it deterministically
/// captures each measurement (instrument name + value + tags) as it is recorded,
/// with no dependence on the Prometheus exporter's on-demand collect timing (a
/// lazily-created static Meter does not reliably render in a single in-process
/// /metrics scrape; the periodic production scrape does, same as DisputeCaseTelemetry).
///
/// The two auth-sign-in outcomes (verify-failure 401 and lockout 429) are driven
/// END-TO-END through /v1/auth/otp/verify — this is the AC's "scripted lockout"
/// smoke, and the tag assertions prove the requests hit the instrumented branches
/// (a generic gateway 429 rate-limit would NOT record auth.otp.lockouts). The
/// remaining three counters (refresh-reuse, handover escalation, durable-writer
/// failure) live on decision points that need a full multi-step token-rotate /
/// handover-lockout / failing-store flow; those sites are verified by adversarial
/// code review, and here we assert their instrument registration + names.
///
/// Prometheus rendering follows the standard OTel transform (lowercase, '.'/'-'
/// -> '_', counters get a '_total' suffix), so e.g. auth.otp.verify_failures is
/// scraped as auth_otp_verify_failures_total.
/// </summary>
public sealed class BusinessOutcomeMetricsEndpointTests
{
    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private sealed record Measurement(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags);

    [Fact]
    public async Task BusinessOutcome_Counters_Fire_At_Real_Auth_Decision_Points()
    {
        var measurements = new List<Measurement>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copied = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copied[tag.Key] = tag.Value;
            lock (measurements)
                measurements.Add(new Measurement(instrument.Name, value, copied));
        });
        listener.Start();

        await using var factory = MakeFactory();
        var client = factory.CreateClient();

        // Real drive #1: failed OTP verify (upstream 401) -> auth.otp.verify_failures.
        var verifyFail = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000099", "code": "9999" }"""));
        verifyFail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Real drive #2: upstream verify lockout (429) -> auth.otp.lockouts.
        var verifyLock = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000099", "code": "0429" }"""));
        verifyLock.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        List<Measurement> captured;
        lock (measurements)
            captured = measurements.ToList();

        // Deterministic end-to-end wiring proof for the two auth decision points.
        captured.Should().Contain(m =>
            m.Instrument == "auth.otp.verify_failures" && m.Value == 1
            && m.Tags.ContainsKey("outcome") && (string)m.Tags["outcome"]! == "invalid_otp",
            "a failed OTP verify must record the verify-failure counter");
        captured.Should().Contain(m =>
            m.Instrument == "auth.otp.lockouts" && m.Value == 1
            && m.Tags.ContainsKey("outcome") && (string)m.Tags["outcome"]! == "too_many_attempts",
            "an upstream OTP lockout (429) must record the lockout counter");

        // All five counters are registered under the expected dotted instrument names.
        BusinessOutcomeTelemetry.OtpVerifyFailures.Name.Should().Be("auth.otp.verify_failures");
        BusinessOutcomeTelemetry.OtpLockouts.Name.Should().Be("auth.otp.lockouts");
        BusinessOutcomeTelemetry.RefreshReuseDetected.Name.Should().Be("auth.refresh.reuse_detected");
        BusinessOutcomeTelemetry.HandoverEscalations.Name.Should().Be("handover.escalations");
        BusinessOutcomeTelemetry.DurableWriteFailures.Name.Should().Be("durable.write_failures");
    }

    private static WebApplicationFactory<Program> MakeFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubServiceOtpClient());
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = true);
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test";
                    o.TtlSeconds = 300;
                });
            });
        });

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        private static readonly IReadOnlyDictionary<string, IEnumerable<string>> ThrottleHeaders =
            new Dictionary<string, IEnumerable<string>> { ["Retry-After"] = new[] { "30" } };

        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            // code "0429" simulates the upstream verify-attempt lockout (429);
            // any other code simulates a wrong/expired code (uniform 401).
            if (body?.Otp == "0429")
                throw new ApiException("throttled", (int)HttpStatusCode.TooManyRequests, "{}", ThrottleHeaders, null);
            throw new ApiException("unauthorized", (int)HttpStatusCode.Unauthorized, "{}", EmptyHeaders, null);
        }
    }
}
