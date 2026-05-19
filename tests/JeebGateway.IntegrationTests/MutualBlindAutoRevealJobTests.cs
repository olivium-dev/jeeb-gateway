using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Push;
using JeebGateway.Ratings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-BE-025 / JEB-61 — coverage for the daily mutual-blind auto-reveal cron.
///
/// These tests bypass the HTTP host and the BackgroundService scheduling
/// loop by invoking <see cref="MutualBlindAutoRevealJob.RunOnceAsync"/>
/// directly against an isolated <see cref="ServiceCollection"/>. That keeps
/// the tests deterministic (no real timer waits) and the assertions tight
/// to the JEB-61 acceptance criteria.
/// </summary>
public class MutualBlindAutoRevealJobTests
{
    private static readonly DateTimeOffset SamiKamalDeliveredAt =
        new(2026, 5, 1, 14, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// AC1 [Gherkin]: Given a delivery rated only by Sami 8 days ago,
    /// When the cron runs, Then state=auto_revealed and Kamal gets a
    /// notification.
    /// </summary>
    [Fact]
    public async Task AC1_Delivery_Rated_Only_By_Sami_8_Days_Ago_Is_Auto_Revealed_And_Kamal_Is_Notified()
    {
        var sami = "user-sami";        // jeeber side — DID rate
        var kamal = "user-kamal";      // client side — did NOT rate
        var deliveryId = "delivery-jeb61-ac1";

        var now = SamiKamalDeliveredAt.AddDays(8);
        var clock = new FakeClock(now);
        var (ratings, push, job) = BuildFixture(clock);

        await ratings.EnsureAsync(deliveryId, kamal, sami, SamiKamalDeliveredAt, default);
        await ratings.SubmitAsync(
            deliveryId,
            callerIsClient: false,
            new RatingEntry(sami, Stars: 4, Comment: "ok run", SamiKamalDeliveredAt.AddMinutes(10)),
            default);

        var revealed = await job.RunOnceAsync(default);

        revealed.Should().Be(1);

        var pair = await ratings.GetAsync(deliveryId, default);
        pair.Should().NotBeNull();
        pair!.AutoRevealedAt.Should().Be(now,
            "the cron must stamp the row exactly once with the current clock");

        // The non-rating party (Kamal) sees state=auto_revealed.
        var kamalView = BlindRevealPolicy.ProjectFor(
            now, pair.DeliveredAt,
            callerIsClient: true,
            pair.ClientRating, pair.JeeberRating,
            TimeSpan.FromDays(7),
            pair.AutoRevealedAt);
        RatingStateCodes.For(kamalView.Outcome).Should().Be(RatingStateCodes.AutoRevealed);
        kamalView.MyRating.Should().BeNull("Kamal never submitted — no synthetic score");
        kamalView.TheirRating.Should().NotBeNull("Sami's score becomes visible after auto-reveal");
        kamalView.TheirRating!.Stars.Should().Be(4);

        // Sami also sees state=auto_revealed but with his own score on his side.
        var samiView = BlindRevealPolicy.ProjectFor(
            now, pair.DeliveredAt,
            callerIsClient: false,
            pair.ClientRating, pair.JeeberRating,
            TimeSpan.FromDays(7),
            pair.AutoRevealedAt);
        RatingStateCodes.For(samiView.Outcome).Should().Be(RatingStateCodes.AutoRevealed);
        samiView.MyRating!.Stars.Should().Be(4);
        samiView.TheirRating.Should().BeNull("Kamal still never submitted");

        // Kamal received exactly one rating_auto_revealed push.
        push.SentTo(kamal).Should().ContainSingle(
            "Kamal is the rated (non-submitting) party per JEB-61");
        var pushed = push.SentTo(kamal).Single();
        pushed.Trigger.Should().Be(NotificationTrigger.RatingAutoRevealed);
        pushed.Data!["template"].Should().Be("rating_auto_revealed");
        pushed.Data["deliveryId"].Should().Be(deliveryId);
        pushed.IdempotencyKey.Should().Be($"rating-auto-reveal:{deliveryId}:{kamal}");

        // Sami (who DID submit) is NOT re-pinged for the auto-reveal.
        push.SentTo(sami).Should().BeEmpty();
    }

    /// <summary>
    /// AC2 [Gherkin]: Given the cron runs twice, When the second run lands,
    /// Then no double reveal (idempotent).
    /// </summary>
    [Fact]
    public async Task AC2_Second_Cron_Run_Does_Not_Double_Reveal_Or_Re_Notify()
    {
        var deliveryId = "delivery-jeb61-ac2";
        var client = "user-client-ac2";
        var jeeber = "user-jeeber-ac2";

        var now = SamiKamalDeliveredAt.AddDays(8);
        var clock = new FakeClock(now);
        var (ratings, push, job) = BuildFixture(clock);

        await ratings.EnsureAsync(deliveryId, client, jeeber, SamiKamalDeliveredAt, default);
        // No side submitted — both lapsed. Both will be notified on the FIRST run.

        var firstRun = await job.RunOnceAsync(default);
        firstRun.Should().Be(1, "first run discovers and reveals the row");
        push.AllSent.Should().HaveCount(2, "both lapsed parties get the auto-reveal ping on first run");
        var firstStamp = (await ratings.GetAsync(deliveryId, default))!.AutoRevealedAt;
        firstStamp.Should().NotBeNull();

        // Advance the clock a few hours and run again — simulating the next
        // daily tick landing on the same already-revealed row.
        clock.Advance(TimeSpan.FromHours(24));
        var secondRun = await job.RunOnceAsync(default);
        secondRun.Should().Be(0, "AC2 — no double reveal");
        push.AllSent.Should().HaveCount(2, "AC2 — no double notification");
        var afterSecond = (await ratings.GetAsync(deliveryId, default))!;
        afterSecond.AutoRevealedAt.Should().Be(firstStamp,
            "stamp must remain the FIRST reveal instant — idempotency contract");
    }

    /// <summary>
    /// AC3 [observability]: Logs revealedCount. We capture the structured
    /// log fields and assert <c>revealedCount</c> is emitted with the
    /// correct value.
    /// </summary>
    [Fact]
    public async Task AC3_Cron_Logs_RevealedCount_As_Structured_Field()
    {
        var clock = new FakeClock(SamiKamalDeliveredAt.AddDays(8));
        var logger = new CapturingLogger<MutualBlindAutoRevealJob>();
        var (ratings, _, job) = BuildFixture(clock, logger);

        await ratings.EnsureAsync("d1", "c1", "j1", SamiKamalDeliveredAt, default);
        await ratings.EnsureAsync("d2", "c2", "j2", SamiKamalDeliveredAt, default);
        await ratings.SubmitAsync(
            "d2", callerIsClient: true,
            new RatingEntry("c2", 5, null, SamiKamalDeliveredAt.AddMinutes(1)),
            default);

        var revealed = await job.RunOnceAsync(default);

        revealed.Should().Be(2);
        var completion = logger.Entries
            .Single(e => e.Message.StartsWith("auto_reveal_ratings completed", StringComparison.Ordinal));
        completion.State["RevealedCount"].Should().Be(2);
        completion.State.Should().ContainKey("NotifiedCount");
        completion.State.Should().ContainKey("ScannedCount");
    }

    /// <summary>
    /// Sanity: rows still inside the 7-day window are NEVER touched, even
    /// when one side has already submitted (that's the normal blind state).
    /// </summary>
    [Fact]
    public async Task Rows_Inside_Window_Are_Not_Auto_Revealed()
    {
        var clock = new FakeClock(SamiKamalDeliveredAt.AddDays(3)); // well inside window
        var (ratings, push, job) = BuildFixture(clock);

        await ratings.EnsureAsync("d-fresh", "client", "jeeber", SamiKamalDeliveredAt, default);
        await ratings.SubmitAsync(
            "d-fresh", callerIsClient: true,
            new RatingEntry("client", 5, null, SamiKamalDeliveredAt.AddMinutes(1)),
            default);

        var revealed = await job.RunOnceAsync(default);

        revealed.Should().Be(0);
        push.AllSent.Should().BeEmpty();
        (await ratings.GetAsync("d-fresh", default))!.AutoRevealedAt.Should().BeNull();
    }

    /// <summary>
    /// Sanity: rows where BOTH parties already submitted within the window
    /// have already revealed themselves via the mutual-consent path; the
    /// auto-reveal cron must skip them so we don't fire the wrong template.
    /// </summary>
    [Fact]
    public async Task Mutually_Revealed_Rows_Are_Not_Auto_Revealed()
    {
        var clock = new FakeClock(SamiKamalDeliveredAt.AddDays(30)); // well past window
        var (ratings, push, job) = BuildFixture(clock);

        await ratings.EnsureAsync("d-mutual", "client", "jeeber", SamiKamalDeliveredAt, default);
        await ratings.SubmitAsync(
            "d-mutual", callerIsClient: true,
            new RatingEntry("client", 5, null, SamiKamalDeliveredAt.AddMinutes(1)),
            default);
        await ratings.SubmitAsync(
            "d-mutual", callerIsClient: false,
            new RatingEntry("jeeber", 4, null, SamiKamalDeliveredAt.AddMinutes(2)),
            default);

        var revealed = await job.RunOnceAsync(default);

        revealed.Should().Be(0,
            "AC2 spirit — a row that genuinely got both ratings has already revealed itself");
        push.AllSent.Should().BeEmpty();
        (await ratings.GetAsync("d-mutual", default))!.AutoRevealedAt
            .Should().BeNull("we never stamp a row that didn't need the cron");
    }

    /// <summary>
    /// Push failure must NOT roll back the reveal stamp. The row is
    /// authoritatively revealed; the push retry policy belongs to
    /// PushNotificationService (T-backend-022).
    /// </summary>
    [Fact]
    public async Task Push_Failure_Does_Not_Rollback_Reveal_Stamp()
    {
        var clock = new FakeClock(SamiKamalDeliveredAt.AddDays(8));
        var (ratings, push, job) = BuildFixture(clock, failPush: true);

        await ratings.EnsureAsync("d-push-fail", "c", "j", SamiKamalDeliveredAt, default);

        var revealed = await job.RunOnceAsync(default);

        revealed.Should().Be(1, "reveal counts even when push fan-out fails");
        (await ratings.GetAsync("d-push-fail", default))!.AutoRevealedAt
            .Should().NotBeNull("push failure must not roll back the stamp");
    }

    /// <summary>
    /// ComputeNextDelay must land on the next 03:00 Asia/Beirut boundary —
    /// the spec line. Asserts using a UTC reference where 03:00 Beirut
    /// summer time (UTC+3) maps to 00:00 UTC.
    /// </summary>
    [Fact]
    public void ComputeNextDelay_Targets_03_Beirut_Local()
    {
        // 2026-05-15 12:00 UTC == 2026-05-15 15:00 Beirut (UTC+3 in May).
        // Next 03:00 Beirut is 2026-05-16 03:00 +03 == 2026-05-16 00:00 UTC.
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        var optsHolder = new TestOptionsMonitor<MutualBlindAutoRevealOptions>(
            new MutualBlindAutoRevealOptions { OverrideTickInterval = null });
        var job = new MutualBlindAutoRevealJob(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            clock, optsHolder, NullLogger<MutualBlindAutoRevealJob>.Instance);

        var delay = job.ComputeNextDelay(optsHolder.CurrentValue);

        // 2026-05-15 12:00 UTC → 2026-05-16 00:00 UTC is 12h
        // (allow ±1s slack for DST edge cases).
        delay.Should().BeCloseTo(TimeSpan.FromHours(12), TimeSpan.FromSeconds(1));
    }

    // -----------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------

    private static (IRatingStore ratings, RecordingPushService push, MutualBlindAutoRevealJob job) BuildFixture(
        FakeClock clock,
        CapturingLogger<MutualBlindAutoRevealJob>? logger = null,
        bool failPush = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IRatingStore, InMemoryRatingStore>();
        var push = new RecordingPushService(failPush);
        services.AddSingleton<IPushNotificationService>(push);
        var opts = new TestOptionsMonitor<MutualBlindAutoRevealOptions>(
            new MutualBlindAutoRevealOptions
            {
                RatingWindow = TimeSpan.FromDays(7),
                OverrideTickInterval = TimeSpan.FromHours(24),
            });
        services.AddSingleton<IOptionsMonitor<MutualBlindAutoRevealOptions>>(opts);
        services.AddSingleton(logger ?? new CapturingLogger<MutualBlindAutoRevealJob>());
        services.AddSingleton(sp => new MutualBlindAutoRevealJob(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptionsMonitor<MutualBlindAutoRevealOptions>>(),
            sp.GetRequiredService<CapturingLogger<MutualBlindAutoRevealJob>>()));

        var provider = services.BuildServiceProvider();
        return (
            provider.GetRequiredService<IRatingStore>(),
            push,
            provider.GetRequiredService<MutualBlindAutoRevealJob>());
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingPushService : IPushNotificationService
    {
        private readonly ConcurrentQueue<PushNotificationRequest> _sent = new();
        private readonly bool _fail;

        public RecordingPushService(bool fail) => _fail = fail;

        public IReadOnlyList<PushNotificationRequest> AllSent => _sent.ToArray();

        public IReadOnlyList<PushNotificationRequest> SentTo(string userId)
            => _sent.Where(r => r.UserId == userId).ToArray();

        public Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
        {
            if (_fail)
            {
                throw new InvalidOperationException("simulated push transport failure");
            }
            _sent.Enqueue(request);
            return Task.FromResult(new PushDeliveryResult(
                request.UserId, request.Trigger, PushDeliveryOutcome.Delivered, 1));
        }
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<LogEntry> _entries = new();
        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kvp in kvps)
                {
                    dict[kvp.Key] = kvp.Value;
                }
            }
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), dict));
            }
        }

        public sealed record LogEntry(
            Microsoft.Extensions.Logging.LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> State);

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
        }
    }
}
