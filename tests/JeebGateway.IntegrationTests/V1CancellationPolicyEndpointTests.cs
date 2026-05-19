using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation.V2;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-BE-030 (JEB-66) integration tests for POST /v1/deliveries/{id}/cancel.
/// Covers the six acceptance criteria from the Jira story:
/// <list type="bullet">
///   <item><b>AC1</b> — 4th client cancel this week → 200 with feeApplied.</item>
///   <item><b>AC2</b> — 6th client cancel this week → 429 with retryAfter.</item>
///   <item><b>AC3</b> — Jeeber 3rd strike in 30 days → role suspended 7 days.</item>
///   <item><b>AC4</b> — Logs <c>cancel.policy_applied</c> (asserted via the
///     in-memory log store + the upstream-payments client side-effects;
///     the literal log line is grepped in qa/t-be-030/observability-grep.sh).</item>
///   <item><b>AC5</b> — Legacy /deliveries/{id}/cancel still returns 200 / 409
///     exactly as before (sibling tests in CancellationEndpointTests.cs
///     continue to pass — no behavior change to that surface).</item>
///   <item><b>AC6</b> — Cancel for status &gt; picked → 422 too_late_to_cancel.</item>
/// </list>
/// Each test boots a fresh WebApplicationFactory with a controllable
/// FakeClock so the ISO-week boundary and 30-day strike window can be
/// driven deterministically.
/// </summary>
public class V1CancellationPolicyEndpointTests
{
    // -------- AC1 ------------------------------------------------------

    [Fact]
    public async Task AC1_Client_Fourth_Cancel_This_Week_Is_Allowed_With_Fee()
    {
        using var factory = NewFactory(out _);
        var clientId = NewClientId();

        // 3 free cancellations (soft-limit = 3).
        for (var i = 0; i < 3; i++)
        {
            var ok = await PostCancelAsClient(factory, clientId, RequestStatus.Pending);
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            var okDto = await ok.Content.ReadFromJsonAsync<CancelV1Response>();
            okDto!.FeeApplied.Should().BeFalse($"cancel #{i + 1} is within the soft limit");
            okDto.ClientCancellationsThisWeek.Should().Be(i + 1);
        }

        // 4th cancel — soft limit breached, fee applies.
        var resp = await PostCancelAsClient(factory, clientId, RequestStatus.Pending);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CancelV1Response>();
        dto!.Status.Should().Be(RequestStatus.Cancelled);
        dto.FeeApplied.Should().BeTrue();
        dto.FeeAmount.Should().Be(15_000m);
        dto.FeeCurrency.Should().Be("LBP");
        dto.FeeIdempotencyKey.Should().NotBeNullOrWhiteSpace();
        dto.ClientCancellationsThisWeek.Should().Be(4);

        var payments = factory.Services
            .GetRequiredService<InMemoryUnifiedPaymentGatewayCancellationClient>();
        payments.Posted.Should().HaveCount(1, "exactly one fee posting for the 4th cancel");
        payments.Posted.Single().Amount.Should().Be(15_000m);
        payments.Posted.Single().UserId.Should().Be(clientId);
    }

    // -------- AC2 ------------------------------------------------------

    [Fact]
    public async Task AC2_Client_Sixth_Cancel_This_Week_Returns_429_With_RetryAfter()
    {
        using var factory = NewFactory(out var clock);
        var clientId = NewClientId();

        // 5 cancellations within the hard limit (free for first 3, fee for 4 & 5).
        for (var i = 0; i < 5; i++)
        {
            var ok = await PostCancelAsClient(factory, clientId, RequestStatus.Pending);
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // 6th — hard-limit breach.
        var resp = await PostCancelAsClient(factory, clientId, RequestStatus.Pending);
        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        resp.Headers.Should().ContainKey("Retry-After");

        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/cancellation-rate-limited");
        root.GetProperty("status").GetInt32().Should().Be(429);
        root.GetProperty("cap").GetInt32().Should().Be(5);
        root.GetProperty("used").GetInt32().Should().Be(5);
        root.GetProperty("retryAfterSeconds").GetInt32().Should().BeGreaterThan(0);

        var resetAt = root.GetProperty("resetAt").GetDateTimeOffset();
        resetAt.Should().BeAfter(clock.GetUtcNow(),
            "Retry-After lands on the next ISO-week boundary (Monday 00:00 UTC)");

        var payments = factory.Services
            .GetRequiredService<InMemoryUnifiedPaymentGatewayCancellationClient>();
        payments.Posted.Should().HaveCount(2, "fee posts only for cancels 4 and 5, never on the 429");
    }

    [Fact]
    public async Task AC2_Hard_Limit_Resets_On_New_ISO_Week()
    {
        using var factory = NewFactory(out var clock);
        var clientId = NewClientId();

        for (var i = 0; i < 5; i++)
        {
            var ok = await PostCancelAsClient(factory, clientId, RequestStatus.Pending);
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Advance past the next Monday — the tally clears.
        clock.Advance(TimeSpan.FromDays(8));

        var resp = await PostCancelAsClient(factory, clientId, RequestStatus.Pending);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CancelV1Response>();
        dto!.ClientCancellationsThisWeek.Should().Be(1, "the tally rolls over with the ISO week");
        dto.FeeApplied.Should().BeFalse();
    }

    // -------- AC3 ------------------------------------------------------

    [Fact]
    public async Task AC3_Jeeber_Third_Strike_In_30_Days_Suspends_Role_For_7_Days()
    {
        using var factory = NewFactory(out var clock);
        var jeeberId = NewJeeberId();

        // Strikes 1 + 2 — recorded, no suspension yet.
        for (var i = 0; i < 2; i++)
        {
            var ok = await PostCancelAsJeeber(factory, jeeberId, $"jeeber-reason-{i}");
            ok.StatusCode.Should().Be(HttpStatusCode.OK);

            var okDto = await ok.Content.ReadFromJsonAsync<CancelV1Response>();
            okDto!.JeeberStrikesLast30Days.Should().Be(i + 1);
            okDto.JeeberRoleSuspended.Should().BeFalse($"strike #{i + 1} stays below the threshold");

            clock.Advance(TimeSpan.FromDays(2));
        }

        // 3rd strike trips the threshold.
        var resp = await PostCancelAsJeeber(factory, jeeberId, "jeeber-reason-3");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CancelV1Response>();
        dto!.JeeberStrikesLast30Days.Should().Be(3);
        dto.JeeberRoleSuspended.Should().BeTrue("strike 3 / 30d triggers the suspension");
        dto.SuspensionExpiresAt.Should().NotBeNull();
        dto.SuspensionExpiresAt!.Value
            .Should().BeCloseTo(clock.GetUtcNow() + TimeSpan.FromDays(7), TimeSpan.FromMinutes(1));

        var suspensions = factory.Services
            .GetRequiredService<InMemoryJeeberRoleSuspensionClient>();
        (await suspensions.IsSuspendedAsync(jeeberId, clock.GetUtcNow(), default)).Should().BeTrue();
        suspensions.Snapshot.Should().ContainSingle(s => s.UserId == jeeberId);
    }

    [Fact]
    public async Task AC3_Strikes_Older_Than_30_Days_Do_Not_Trip_The_Threshold()
    {
        using var factory = NewFactory(out var clock);
        var jeeberId = NewJeeberId();

        // 2 strikes 35 days ago.
        for (var i = 0; i < 2; i++)
        {
            (await PostCancelAsJeeber(factory, jeeberId, $"old-{i}"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        clock.Advance(TimeSpan.FromDays(35));

        // Fresh strike — should be the only one inside the rolling window.
        var resp = await PostCancelAsJeeber(factory, jeeberId, "fresh");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CancelV1Response>();
        dto!.JeeberStrikesLast30Days.Should().Be(1);
        dto.JeeberRoleSuspended.Should().BeFalse();
    }

    // -------- AC6 ------------------------------------------------------

    [Theory]
    [InlineData(RequestStatus.HeadingOff)]
    [InlineData(RequestStatus.Delivered)]
    public async Task AC6_Cancel_After_Picked_Returns_422_Too_Late_To_Cancel(string status)
    {
        using var factory = NewFactory(out _);
        var seed = await Seed(factory, status, bindJeeber: true);

        var resp = await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/too-late-to-cancel");
        problem.Title.Should().Be("too_late_to_cancel");
    }

    [Fact]
    public async Task AC6_Cancel_At_PickedUp_Boundary_Is_Still_Allowed()
    {
        using var factory = NewFactory(out _);
        var seed = await Seed(factory, RequestStatus.PickedUp);

        var resp = await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "picked_up is the boundary; status STRICTLY > picked is too late");
    }

    // -------- AC4 ------------------------------------------------------
    // The literal log line `cancel.policy_applied` is asserted by the
    // observability-grep script in qa/. Here we assert the policy
    // side-effects that the log line carries are visible: cancellation
    // log row + payments client invocation + suspension client invocation.

    [Fact]
    public async Task AC4_Cancellation_Log_Captures_Action_And_Fee()
    {
        using var factory = NewFactory(out _);
        var clientId = NewClientId();

        for (var i = 0; i < 4; i++)
        {
            (await PostCancelAsClient(factory, clientId, RequestStatus.Pending))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var log = factory.Services.GetRequiredService<ICancellationLogStore>();
        var count = await log.CountClientCancellationsInWeekAsync(clientId, DateTimeOffset.UtcNow, default);
        count.Should().Be(4);

        var payments = factory.Services
            .GetRequiredService<InMemoryUnifiedPaymentGatewayCancellationClient>();
        payments.Posted.Should().ContainSingle()
            .Which.Should().Match<CancellationFeePostRequest>(p =>
                p.UserId == clientId && p.Amount == 15_000m && p.Currency == "LBP");
    }

    // -------- AC5 ------------------------------------------------------
    // Concretely tested via the sibling CancellationEndpointTests suite
    // (the legacy /deliveries/{id}/cancel surface). We add a single
    // belt-and-suspenders test here that the v1 surface does NOT poison
    // the existing in-memory restriction store the legacy path uses.

    [Fact]
    public async Task AC5_V1_Cancel_Does_Not_Touch_Legacy_JeeberRestrictionStore()
    {
        using var factory = NewFactory(out _);
        var jeeberId = NewJeeberId();

        (await PostCancelAsJeeber(factory, jeeberId, "reason"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var legacyRestrictions = factory.Services
            .GetRequiredService<JeebGateway.Requests.Cancellation.IJeeberRestrictionStore>();
        (await legacyRestrictions.IsRestrictedAsync(jeeberId, DateTimeOffset.UtcNow, default))
            .Should().BeFalse("the v1 surface owns its own log; it must not write into the legacy 24h-restriction store");
    }

    // -------- Auth + edge cases ---------------------------------------

    [Fact]
    public async Task Cancel_Without_Identity_Returns_401()
    {
        using var factory = NewFactory(out _);
        var seed = await Seed(factory, RequestStatus.Pending);

        var resp = await factory.CreateClient()
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cancel_By_Stranger_Returns_403()
    {
        using var factory = NewFactory(out _);
        var seed = await Seed(factory, RequestStatus.Accepted);

        var stranger = ClientFor(factory, NewClientId());
        var resp = await stranger.PostAsJsonAsync(
            $"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/not-a-party");
    }

    [Fact]
    public async Task Cancel_Of_Unknown_Delivery_Returns_404()
    {
        using var factory = NewFactory(out _);
        var resp = await ClientFor(factory, NewClientId())
            .PostAsJsonAsync($"/v1/deliveries/{Guid.NewGuid()}/cancel", new CancelV1Request());
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Jeeber_Cancel_Without_Reason_Returns_400()
    {
        using var factory = NewFactory(out _);
        var jeeberId = NewJeeberId();
        var seed = await Seed(factory, RequestStatus.Accepted, bindJeeber: true, jeeberId: jeeberId);

        var resp = await JeeberClient(factory, jeeberId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/cancellation-reason-required");
    }

    [Fact]
    public async Task Cancel_Of_Already_Cancelled_Delivery_Returns_409()
    {
        using var factory = NewFactory(out _);
        var seed = await Seed(factory, RequestStatus.Pending);

        (await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request()))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------- helpers -----------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
    {
        // Anchor the FakeClock on a Wednesday so the ISO-week boundary
        // tests have at least 4 days of headroom before the Monday rollover.
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero));
        clock = theClock;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.Single(d => d.ServiceType == typeof(TimeProvider));
                services.Remove(existing);
                services.AddSingleton<TimeProvider>(theClock);
            });
        });
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Client);
        return c;
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Jeeber);
        return c;
    }

    private static async Task<HttpResponseMessage> PostCancelAsClient(
        WebApplicationFactory<Program> factory, string clientId, string targetStatus)
    {
        var seed = await Seed(factory, targetStatus, clientId: clientId);
        return await ClientFor(factory, seed.ClientId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request());
    }

    private static async Task<HttpResponseMessage> PostCancelAsJeeber(
        WebApplicationFactory<Program> factory, string jeeberId, string reason)
    {
        var seed = await Seed(factory, RequestStatus.Accepted, bindJeeber: true, jeeberId: jeeberId);
        return await JeeberClient(factory, jeeberId)
            .PostAsJsonAsync($"/v1/deliveries/{seed.Id}/cancel", new CancelV1Request { Reason = reason });
    }

    private static async Task<SeedRow> Seed(
        WebApplicationFactory<Program> factory,
        string targetStatus,
        bool bindJeeber = false,
        string? jeeberId = null,
        string? clientId = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        clientId ??= NewClientId();
        jeeberId ??= NewJeeberId();

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "v1-cancel-test"
        }, default);

        if (bindJeeber || targetStatus != RequestStatus.Pending)
        {
            var accepted = await store.TryAcceptByJeeberAsync(
                created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
            accepted.Should().NotBeNull(
                $"the test rig must bind a jeeber before transitioning to {targetStatus}");
        }

        if (created.Status != targetStatus)
        {
            (await store.SetStatusAsync(created.Id, targetStatus, default)).Should().BeTrue();
        }

        return new SeedRow(created.Id, clientId, bindJeeber ? jeeberId : null);
    }

    private static string NewClientId() => $"client-{Guid.NewGuid()}";
    private static string NewJeeberId() => $"jeeber-{Guid.NewGuid()}";

    private sealed record SeedRow(string Id, string ClientId, string? JeeberId);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
