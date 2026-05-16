using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-046 (Phase 2): scheduled delivery.
///
/// Acceptance criteria covered:
///   * Client can set a future delivery date/time on creation.
///   * Matching is triggered at <c>scheduled_at - 30 min</c> (status flips
///     scheduled → pending, kicking off the existing matching pipeline).
///   * Client reminder fires the same instant matching opens.
///   * Cancellation rules match the immediate-delivery path: the owning
///     Client may cancel at any non-terminal state; cancelling frees a
///     BR-9 slot.
///
/// Each test gets a fresh factory (and therefore a fresh in-memory store
/// + notifier + clock) so cases don't share state.
/// </summary>
public class ScheduledDeliveryTests
{
    [Fact]
    public async Task Create_Scheduled_Request_Starts_In_Scheduled_Status()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "sched-create-client");

        var scheduledAt = clock.GetUtcNow() + TimeSpan.FromHours(2);
        var resp = await client.PostAsJsonAsync("/requests", ValidBody("Pick up groceries at 2pm", scheduledAt));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        dto!.Status.Should().Be(RequestStatus.Scheduled);
        dto.ScheduledAt.Should().Be(scheduledAt);
    }

    [Fact]
    public async Task Create_Immediate_Request_Still_Defaults_To_Pending()
    {
        var factory = NewFactory(out _);
        var client = ClientFor(factory, "sched-immediate-client");

        var resp = await client.PostAsJsonAsync("/requests", ValidBody("now"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        dto!.Status.Should().Be(RequestStatus.Pending);
        dto.ScheduledAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_Rejects_Past_Or_Now_ScheduledAt()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "sched-past-client");

        var pastResp = await client.PostAsJsonAsync("/requests",
            ValidBody("back in time", clock.GetUtcNow() - TimeSpan.FromMinutes(1)));
        pastResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await pastResp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Contain("future");

        var nowResp = await client.PostAsJsonAsync("/requests",
            ValidBody("right now", clock.GetUtcNow()));
        nowResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Matching_Triggers_Thirty_Minutes_Before_Scheduled_Time()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "sched-match-client");

        var scheduledAt = clock.GetUtcNow() + TimeSpan.FromHours(2);
        var requestId = await CreateScheduled(client, "Birthday gift drop", scheduledAt);

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var notifier = (InMemoryScheduledDeliveryNotifier)
            factory.Services.GetRequiredService<IScheduledDeliveryNotifier>();

        // T-31min — still inside the scheduled window; activator must NOT fire.
        clock.Advance(TimeSpan.FromMinutes(89));
        await ActivateOnce(factory);
        notifier.ClientReminders.Should().BeEmpty();
        (await store.GetAsync(requestId, CancellationToken.None))!
            .Status.Should().Be(RequestStatus.Scheduled);

        // Crossing the T-30 mark fires matching + reminder exactly once.
        clock.Advance(TimeSpan.FromMinutes(2));
        await ActivateOnce(factory);

        var activated = (await store.GetAsync(requestId, CancellationToken.None))!;
        activated.Status.Should().Be(RequestStatus.Pending);
        activated.ActivatedAt.Should().NotBeNull();

        notifier.ClientReminders.Should().ContainSingle()
            .Which.Should().Match<InMemoryScheduledDeliveryNotifier.ClientReminderRecord>(
                r => r.RequestId == requestId
                  && r.ClientId == "sched-match-client"
                  && r.ScheduledAt == scheduledAt);

        // Idempotence — a follow-up sweep inside the window must not
        // re-send the prompt to the Client.
        await ActivateOnce(factory);
        notifier.ClientReminders.Should().HaveCount(1);
    }

    [Fact]
    public async Task Activator_Fires_Immediately_When_Scheduled_Window_Already_Open_On_Creation()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "sched-short-notice-client");

        // Short-notice schedule — only 10 minutes out, well inside the
        // 30-min matching buffer. The activator should activate on the
        // very next sweep rather than waiting for the wall-clock to catch
        // up to ScheduledAt-30min (which is already in the past).
        var scheduledAt = clock.GetUtcNow() + TimeSpan.FromMinutes(10);
        var requestId = await CreateScheduled(client, "Short notice", scheduledAt);

        await ActivateOnce(factory);

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.GetAsync(requestId, CancellationToken.None))!
            .Status.Should().Be(RequestStatus.Pending);

        var notifier = (InMemoryScheduledDeliveryNotifier)
            factory.Services.GetRequiredService<IScheduledDeliveryNotifier>();
        notifier.ClientReminders.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_Scheduled_Request_Returns_204_And_Frees_BR9_Slot()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "sched-cancel-client");

        var scheduledAt = clock.GetUtcNow() + TimeSpan.FromHours(3);

        // Saturate the BR-9 cap with scheduled requests, then cancel one
        // and prove a fresh request is now accepted. Mirrors the immediate-
        // delivery cancellation parity AC.
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await CreateScheduled(client, $"sched {i}", scheduledAt));
        }

        var blocked = await client.PostAsJsonAsync("/requests",
            ValidBody("blocked-by-cap", scheduledAt));
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var cancel = await client.DeleteAsync($"/requests/{ids[0]}");
        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var freshOk = await client.PostAsJsonAsync("/requests",
            ValidBody("after-cancel", scheduledAt));
        freshOk.StatusCode.Should().Be(HttpStatusCode.Created);

        // Activator must NOT subsequently activate a cancelled scheduled row.
        clock.Advance(scheduledAt - clock.GetUtcNow() - TimeSpan.FromMinutes(20));
        await ActivateOnce(factory);

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        (await store.GetAsync(ids[0], CancellationToken.None))!
            .Status.Should().Be(RequestStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_Rejects_Non_Owner_With_403()
    {
        var factory = NewFactory(out var clock);
        var alice = ClientFor(factory, "sched-owner-alice");
        var mallory = ClientFor(factory, "sched-attacker-mallory");

        var requestId = await CreateScheduled(
            alice,
            "alice gift",
            clock.GetUtcNow() + TimeSpan.FromHours(2));

        var resp = await mallory.DeleteAsync($"/requests/{requestId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cancel_Returns_404_For_Unknown_Id()
    {
        var factory = NewFactory(out _);
        var client = ClientFor(factory, "sched-404-client");

        var resp = await client.DeleteAsync($"/requests/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scheduled_Request_Counts_Toward_BR9_Active_Cap()
    {
        var factory = NewFactory(out var clock);
        var client = ClientFor(factory, "sched-br9-client");

        var scheduledAt = clock.GetUtcNow() + TimeSpan.FromHours(4);

        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/requests",
                ValidBody($"sched {i}", scheduledAt))).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Fourth scheduled MUST hit the BR-9 cap exactly like an immediate
        // request would — scheduled is in the ActiveStates set.
        var blocked = await client.PostAsJsonAsync("/requests",
            ValidBody("fourth scheduled", scheduledAt));
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await blocked.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be(
            "Maximum 3 active requests. Complete or cancel an existing request.");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
    {
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 16, 9, 0, 0, TimeSpan.Zero));
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
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static async Task<string> CreateScheduled(
        HttpClient client,
        string description,
        DateTimeOffset scheduledAt)
    {
        var resp = await client.PostAsJsonAsync("/requests", ValidBody(description, scheduledAt));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<RequestDto>();
        return dto!.Id;
    }

    /// <summary>
    /// Builds a request body that satisfies the T-backend-007 validators
    /// (tier + structured pickup/dropoff). Tests in this file care about
    /// the scheduled-delivery contract; they're not exercising the new
    /// field-level validators, so a single canned valid pickup/dropoff
    /// pair is sufficient.
    /// </summary>
    private static object ValidBody(string description, DateTimeOffset? scheduledAt = null) => new
    {
        description,
        tierId = "flash",
        pickupLocation = new { lat = 24.7136, lng = 46.6753 },
        dropoffLocation = new { lat = 24.6309, lng = 46.7194 },
        scheduledAt
    };

    private static Task ActivateOnce(WebApplicationFactory<Program> factory)
    {
        var activator = factory.Services
            .GetServices<IHostedService>()
            .OfType<ScheduledDeliveryActivator>()
            .Single();
        return activator.SweepOnceAsync(default);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed record RequestDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        string? PickupAddress,
        string? DropoffAddress,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ScheduledAt);
}
