using System.Net;
using System.Net.Http.Json;
using JeebGateway.Controllers;
using JeebGateway.TestControlPlane;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1502 — Test control-plane: unit/integration tests.
///
/// Test matrix:
///   T1  Clock advance composes additively (advance 1d + advance 6d → offset +7d)
///   T2  Clock reset clears the offset
///   T3  Flag OFF → all /__test/* routes return 404
///   T4  Wrong secret → 401
///   T5  Missing secret → 401
///   T6  Empty SharedSecret configured → 401 even when flag is ON
///   T7  Correct secret + flag ON → 200 on GET /__test/clock
///   T8  Job not found → 404
///   T9  Known jobs are listed by GET /__test/jobs
///   T10 GET /__test/clock returns effective now reflecting current offset
/// </summary>
public sealed class TestControlPlaneTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidSecret = "test-plane-secret-abc123";
    private const string WrongSecret = "wrong-secret";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build an HttpClient backed by a test host that has the test control-plane
    /// ENABLED with <see cref="ValidSecret"/>.
    /// </summary>
    private HttpClient BuildEnabledClient(WebApplicationFactory<Program> factory)
        => factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.Configure<TestControlPlaneOptions>(o =>
                {
                    o.Enabled = true;
                    o.SharedSecret = ValidSecret;
                });
            });
        }).CreateClient();

    /// <summary>
    /// Build a client with the plane DISABLED (default) — tests T3.
    /// </summary>
    private HttpClient BuildDisabledClient(WebApplicationFactory<Program> factory)
        => factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.Configure<TestControlPlaneOptions>(o =>
                {
                    o.Enabled = false;
                });
            });
        }).CreateClient();

    private static HttpRequestMessage ClockGet(string? secret = ValidSecret)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/__test/clock");
        if (secret is not null)
            msg.Headers.Add(TestControlPlaneOnlyAttribute.SecretHeaderName, secret);
        return msg;
    }

    private static HttpRequestMessage ClockAdvance(int seconds, string? secret = ValidSecret)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/__test/clock/advance")
        {
            Content = JsonContent.Create(new { durationSeconds = seconds })
        };
        if (secret is not null)
            msg.Headers.Add(TestControlPlaneOnlyAttribute.SecretHeaderName, secret);
        return msg;
    }

    private static HttpRequestMessage ClockReset(string? secret = ValidSecret)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/__test/clock/reset");
        if (secret is not null)
            msg.Headers.Add(TestControlPlaneOnlyAttribute.SecretHeaderName, secret);
        return msg;
    }

    private static HttpRequestMessage JobsList(string? secret = ValidSecret)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/__test/jobs");
        if (secret is not null)
            msg.Headers.Add(TestControlPlaneOnlyAttribute.SecretHeaderName, secret);
        return msg;
    }

    private static HttpRequestMessage JobRun(string name, string? secret = ValidSecret)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"/__test/jobs/{name}/run");
        if (secret is not null)
            msg.Headers.Add(TestControlPlaneOnlyAttribute.SecretHeaderName, secret);
        return msg;
    }

    // -------------------------------------------------------------------------
    // T1: offset composes additively
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T1_Clock_AdvanceTwice_ComposesAdditively()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);
        var fakeProvider = factory.Services.GetRequiredService<FakeTimeProvider>();

        // Reset any prior state
        fakeProvider.Reset();

        var r1 = await client.SendAsync(ClockAdvance(86_400)); // +1d
        r1.EnsureSuccessStatusCode();
        var state1 = await r1.Content.ReadFromJsonAsync<ClockStateResponse>();

        var r2 = await client.SendAsync(ClockAdvance(518_400)); // +6d
        r2.EnsureSuccessStatusCode();
        var state2 = await r2.Content.ReadFromJsonAsync<ClockStateResponse>();

        // Offset should be 7d total
        Assert.NotNull(state2);
        Assert.Equal(TimeSpan.FromDays(7).TotalSeconds, state2.Offset.TotalSeconds, precision: 1);
    }

    // -------------------------------------------------------------------------
    // T2: reset clears offset
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T2_Clock_Reset_ClearsOffset()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);
        var fakeProvider = factory.Services.GetRequiredService<FakeTimeProvider>();

        await client.SendAsync(ClockAdvance(172_800)); // +2d

        var resetResp = await client.SendAsync(ClockReset());
        resetResp.EnsureSuccessStatusCode();
        var state = await resetResp.Content.ReadFromJsonAsync<ClockStateResponse>();

        Assert.NotNull(state);
        Assert.Equal(0, state.Offset.TotalSeconds, precision: 1);

        // Provider itself should also report zero
        Assert.Equal(TimeSpan.Zero, fakeProvider.CurrentOffset);
    }

    // -------------------------------------------------------------------------
    // T3: flag OFF → 404
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T3_FlagOff_AllRoutes_Return404()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildDisabledClient(factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(ClockGet())).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(ClockAdvance(60))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(ClockReset())).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(JobsList())).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(JobRun("rating-reveal"))).StatusCode);
    }

    // -------------------------------------------------------------------------
    // T4: wrong secret → 401
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T4_WrongSecret_Returns401()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);

        var resp = await client.SendAsync(ClockGet(secret: WrongSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // T5: missing secret → 401
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T5_MissingSecret_Returns401()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);

        var resp = await client.SendAsync(ClockGet(secret: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // T6: empty SharedSecret configured → 401 even when flag is ON
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T6_EmptySharedSecret_Returns401()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.Configure<TestControlPlaneOptions>(o =>
                {
                    o.Enabled = true;
                    o.SharedSecret = string.Empty; // intentionally empty
                });
            });
        }).CreateClient();

        var resp = await client.SendAsync(ClockGet(secret: "anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // T7: correct secret + flag ON → 200
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T7_CorrectSecret_Returns200()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);

        var resp = await client.SendAsync(ClockGet());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // T8: unknown job name → 404
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T8_UnknownJobName_Returns404()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);

        var resp = await client.SendAsync(JobRun("nonexistent-job-xyz"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // T9: GET /__test/jobs lists the three registered jobs
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T9_GetJobs_ListsRegisteredJobs()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);

        var resp = await client.SendAsync(JobsList());
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JobListResponse>();
        Assert.NotNull(body);

        var names = body.Jobs.Select(j => j.Name).ToHashSet();
        Assert.Contains("rating-reveal", names);
        Assert.Contains("request-expiry-sweep", names);
        Assert.Contains("settlement-batch", names);
    }

    // -------------------------------------------------------------------------
    // T10: GET /__test/clock reflects the current offset
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T10_GetClock_ReflectsOffset()
    {
        var factory = new WebApplicationFactory<Program>();
        var client = BuildEnabledClient(factory);
        var fakeProvider = factory.Services.GetRequiredService<FakeTimeProvider>();
        fakeProvider.Reset();

        var beforeNow = TimeProvider.System.GetUtcNow();
        await client.SendAsync(ClockAdvance(3600)); // +1h

        var resp = await client.SendAsync(ClockGet());
        resp.EnsureSuccessStatusCode();
        var state = await resp.Content.ReadFromJsonAsync<ClockStateResponse>();

        Assert.NotNull(state);
        // Effective now must be > beforeNow + ~59 min (allow for test overhead)
        Assert.True(state.EffectiveNow >= beforeNow.AddMinutes(59),
            $"Expected effectiveNow to be at least 59 min in the future; got offset {state.Offset}");
    }
}

// -------------------------------------------------------------------------
// Pure unit tests for FakeTimeProvider (no web host required)
// -------------------------------------------------------------------------

public sealed class FakeTimeProviderUnitTests
{
    [Fact]
    public void Advance_ComposesAdditively()
    {
        var provider = new FakeTimeProvider(TimeProvider.System);

        provider.AdvanceBy(TimeSpan.FromDays(3));
        provider.AdvanceBy(TimeSpan.FromDays(4));

        Assert.Equal(TimeSpan.FromDays(7), provider.CurrentOffset);
    }

    [Fact]
    public void Reset_ClearsOffset()
    {
        var provider = new FakeTimeProvider(TimeProvider.System);
        provider.AdvanceBy(TimeSpan.FromDays(10));
        provider.Reset();

        Assert.Equal(TimeSpan.Zero, provider.CurrentOffset);
    }

    [Fact]
    public void GetUtcNow_ReflectsOffset()
    {
        var provider = new FakeTimeProvider(TimeProvider.System);
        var before = TimeProvider.System.GetUtcNow();
        provider.AdvanceBy(TimeSpan.FromHours(24));
        var effective = provider.GetUtcNow();

        Assert.True(effective >= before.AddHours(23).AddMinutes(59),
            "GetUtcNow should reflect the advance offset");
    }

    [Fact]
    public void ZeroOffset_IdenticalToSystemClock()
    {
        var provider = new FakeTimeProvider(TimeProvider.System);
        var systemNow = TimeProvider.System.GetUtcNow();
        var fakeNow = provider.GetUtcNow();

        // Allow 100ms of wall-clock drift between the two calls
        var diff = Math.Abs((fakeNow - systemNow).TotalMilliseconds);
        Assert.True(diff < 100, $"Zero-offset fake should track system clock; diff was {diff}ms");
    }

    [Fact]
    public void NegativeAdvance_Works()
    {
        var provider = new FakeTimeProvider(TimeProvider.System);
        provider.AdvanceBy(TimeSpan.FromDays(7));
        provider.AdvanceBy(TimeSpan.FromDays(-2)); // can step back within the offset

        Assert.Equal(TimeSpan.FromDays(5), provider.CurrentOffset);
    }
}
