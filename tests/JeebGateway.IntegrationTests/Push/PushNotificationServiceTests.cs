using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.NotificationPreferences;
using JeebGateway.Push;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Push;

/// <summary>
/// T-backend-022 acceptance criteria:
///
///   AC1. Push sent for: new offers, acceptance, status changes, chat, KYC,
///        rating reminders.
///   AC2. Delivery within 5 seconds of trigger.
///   AC3. Failed notifications retried once after 30 seconds.
///   AC4. User preference filtering (muted categories not sent).
///
/// Each test gets a fresh factory (and therefore a fresh push transport,
/// retry queue, device token store, and preferences store) so cases don't
/// share state across tests.
/// </summary>
public class PushNotificationServiceTests
{
    [Theory]
    [InlineData(NotificationTrigger.NewOffer)]
    [InlineData(NotificationTrigger.OfferAccepted)]
    [InlineData(NotificationTrigger.StatusChange)]
    [InlineData(NotificationTrigger.Chat)]
    [InlineData(NotificationTrigger.KycUpdate)]
    [InlineData(NotificationTrigger.RatingReminder)]
    [InlineData(NotificationTrigger.Promotion)]
    public async Task Push_Is_Sent_For_Every_Trigger_Category(NotificationTrigger trigger)
    {
        // AC1: every trigger event listed in the AC must fan out to the
        // user's registered device.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-trigger-1", DevicePlatform.Fcm, "tok-fcm");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-trigger-1", trigger, "t", "b"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.Delivered);

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.Sent.Should().ContainSingle()
            .Which.Request.Trigger.Should().Be(trigger);
    }

    [Fact]
    public async Task Push_Delivered_Within_5_Seconds_Of_Trigger()
    {
        // AC2: a happy-path send must complete well under 5 seconds. The
        // DeliverySla CTS lives inside PushNotificationService; this guards
        // against accidental long timeouts being introduced into the path.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-sla", DevicePlatform.Fcm, "tok-fcm");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var sw = Stopwatch.StartNew();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-sla", NotificationTrigger.Chat, "hi", "there"),
            CancellationToken.None);
        sw.Stop();

        result.Outcome.Should().Be(PushDeliveryOutcome.Delivered);
        var opts = factory.Services.GetRequiredService<IOptions<PushOptions>>().Value;
        sw.Elapsed.Should().BeLessThan(opts.DeliverySla,
            "AC2: delivery within 5 seconds of trigger");
    }

    [Fact]
    public async Task Push_Suppressed_When_User_Muted_Category()
    {
        // AC4: muted preferences short-circuit the send — neither attempt
        // fires, the transport never sees the payload, no retry is queued.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-muted", DevicePlatform.Fcm, "tok-fcm");
        var prefs = factory.Services.GetRequiredService<INotificationPreferencesStore>();
        await prefs.UpdateAsync("user-muted", new NotificationPreferencesPatch { Offers = false }, default);

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-muted", NotificationTrigger.NewOffer, "t", "b"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.SuppressedByPreference);
        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.Sent.Should().BeEmpty();

        var queue = factory.Services.GetRequiredService<IPushRetryQueue>();
        queue.PendingCount.Should().Be(0, "suppressed pushes must not be retried");
    }

    [Fact]
    public async Task Kyc_Push_Bypasses_Preference_Mute()
    {
        // KYC is mapped to the always-on bucket — even when every category
        // is muted, the user must still see the KYC notification.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-kyc", DevicePlatform.Apns, "tok-apns");

        var prefs = factory.Services.GetRequiredService<INotificationPreferencesStore>();
        await prefs.UpdateAsync("user-kyc",
            new NotificationPreferencesPatch
            {
                Offers = false, Chat = false, StatusChanges = false, RatingReminders = false, Promotions = false
            }, default);

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-kyc", NotificationTrigger.KycUpdate, "KYC", "approved"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.Delivered);

        var apns = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Apns);
        apns.Sent.Should().ContainSingle()
            .Which.Request.Trigger.Should().Be(NotificationTrigger.KycUpdate);
    }

    [Fact]
    public async Task Failed_First_Attempt_Is_Retried_Once_After_30_Seconds()
    {
        // AC3: the first attempt fails (injected), the entry lands in the
        // retry queue at now+30s; the processor only fires the retry once
        // the clock crosses that mark.
        var factory = NewFactory(out var clock);
        await RegisterDevice(factory, "user-retry", DevicePlatform.Fcm, "tok-fcm");

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.FailNext(1); // first attempt only

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var first = await service.SendAsync(
            new PushNotificationRequest("user-retry", NotificationTrigger.Chat, "hi", "there"),
            CancellationToken.None);

        first.Outcome.Should().Be(PushDeliveryOutcome.QueuedForRetry);
        fcm.Sent.Should().BeEmpty();

        var queue = factory.Services.GetRequiredService<IPushRetryQueue>();
        queue.PendingCount.Should().Be(1);

        var processor = factory.Services.GetRequiredService<PushRetryQueueProcessor>();

        // Just under the 30-second mark — sweeper must NOT fire yet.
        clock.Advance(TimeSpan.FromSeconds(29));
        await processor.ScanOnceAsync(default);
        fcm.Sent.Should().BeEmpty("retry only fires once the 30s window elapses");
        queue.PendingCount.Should().Be(1);

        // Crossing the 30-second mark fires the retry exactly once.
        clock.Advance(TimeSpan.FromSeconds(2));
        await processor.ScanOnceAsync(default);

        fcm.Sent.Should().ContainSingle("retry path delivers the previously failed push");
        queue.PendingCount.Should().Be(0, "retry queue is drained, not re-enqueued");
    }

    [Fact]
    public async Task Failed_Retry_Is_Terminal_And_Not_Retried_Again()
    {
        // AC3 (negative): "retried once" means once — a second failure must
        // be terminal, not the start of an exponential-backoff loop.
        var factory = NewFactory(out var clock);
        await RegisterDevice(factory, "user-retry-fail", DevicePlatform.Fcm, "tok-fcm");

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.FailNext(2); // both attempts

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest("user-retry-fail", NotificationTrigger.Chat, "hi", "there"),
            CancellationToken.None);

        var queue = factory.Services.GetRequiredService<IPushRetryQueue>();
        queue.PendingCount.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(31));
        var processor = factory.Services.GetRequiredService<PushRetryQueueProcessor>();
        await processor.ScanOnceAsync(default);

        fcm.Sent.Should().BeEmpty("both attempts failed in this scenario");
        queue.PendingCount.Should().Be(0, "terminal failure must not re-enqueue");
    }

    [Fact]
    public async Task Apns_And_Fcm_Devices_Both_Receive_Push()
    {
        // A user with one Android + one iOS device must receive the push on
        // both — the dispatcher fans out to every platform-matched transport.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-multi", DevicePlatform.Fcm, "tok-fcm");
        await RegisterDevice(factory, "user-multi", DevicePlatform.Apns, "tok-apns");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest("user-multi", NotificationTrigger.NewOffer, "t", "b"),
            CancellationToken.None);

        var transports = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>().ToArray();
        transports.Single(t => t.Platform == DevicePlatform.Fcm).Sent.Should().ContainSingle();
        transports.Single(t => t.Platform == DevicePlatform.Apns).Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task No_Devices_Returns_NoDevices_Without_Retry()
    {
        // A user with no registered devices is a no-op — no retry queued,
        // no transport calls. Common case for fresh signups before mobile
        // has POSTed the device token.
        var factory = NewFactory(out _);

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-no-device", NotificationTrigger.Chat, "t", "b"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.NoDevices);
        var queue = factory.Services.GetRequiredService<IPushRetryQueue>();
        queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Promotion_Push_Respects_User_Preference()
    {
        // Promotions are user-toggleable — when muted, no push is sent.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-promo-muted", DevicePlatform.Fcm, "tok-fcm");
        var prefs = factory.Services.GetRequiredService<INotificationPreferencesStore>();
        await prefs.UpdateAsync("user-promo-muted", new NotificationPreferencesPatch { Promotions = false }, default);

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-promo-muted", NotificationTrigger.Promotion, "50% off", "Use code JEEB50"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.SuppressedByPreference);
    }

    [Fact]
    public async Task Promotion_Push_Delivered_When_Preference_Enabled()
    {
        // Default preferences enable promotions — the push should land.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-promo-on", DevicePlatform.Fcm, "tok-fcm");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-promo-on", NotificationTrigger.Promotion, "Deal", "body"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.Delivered);
    }

    [Fact]
    public async Task Delivery_Tracker_Records_Every_Outcome()
    {
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-tracked", DevicePlatform.Fcm, "tok-fcm");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest("user-tracked", NotificationTrigger.Chat, "hi", "there"),
            CancellationToken.None);
        await service.SendAsync(
            new PushNotificationRequest("user-tracked", NotificationTrigger.NewOffer, "offer", "body"),
            CancellationToken.None);

        var tracker = factory.Services.GetRequiredService<IPushDeliveryTracker>();
        var deliveries = await tracker.GetForUserAsync("user-tracked", default);
        deliveries.Should().HaveCount(2);
        deliveries.Should().OnlyContain(d => d.Outcome == PushDeliveryOutcome.Delivered);
    }

    [Fact]
    public async Task Delivery_Tracker_Records_Suppressed_Outcome()
    {
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-track-sup", DevicePlatform.Fcm, "tok-fcm");
        var prefs = factory.Services.GetRequiredService<INotificationPreferencesStore>();
        await prefs.UpdateAsync("user-track-sup", new NotificationPreferencesPatch { Chat = false }, default);

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest("user-track-sup", NotificationTrigger.Chat, "hi", "there"),
            CancellationToken.None);

        var tracker = factory.Services.GetRequiredService<IPushDeliveryTracker>();
        var deliveries = await tracker.GetForUserAsync("user-track-sup", default);
        deliveries.Should().ContainSingle()
            .Which.Outcome.Should().Be(PushDeliveryOutcome.SuppressedByPreference);
    }

    [Fact]
    public async Task Deliveries_Tracker_Returns_User_History()
    {
        // Pipeline-level delivery history (was Deliveries_Endpoint_Returns_User_History,
        // which hit the removed PushController GET /push/deliveries). Asserts the
        // same behaviour directly against IPushDeliveryTracker.
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-hist", DevicePlatform.Fcm, "tok-fcm");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest("user-hist", NotificationTrigger.Chat, "hi", "body"),
            CancellationToken.None);

        var tracker = factory.Services.GetRequiredService<IPushDeliveryTracker>();
        var deliveries = await tracker.GetForUserAsync("user-hist", default);
        deliveries.Should().ContainSingle()
            .Which.Outcome.Should().Be(PushDeliveryOutcome.Delivered);
    }

    [Fact]
    public async Task Recent_Deliveries_Tracker_Returns_Cross_User_Results()
    {
        // Pipeline-level cross-user history (was Recent_Deliveries_Endpoint_*,
        // which hit the removed PushController GET /push/deliveries/recent).
        var factory = NewFactory(out _);
        await RegisterDevice(factory, "user-r1", DevicePlatform.Fcm, "tok-1");
        await RegisterDevice(factory, "user-r2", DevicePlatform.Fcm, "tok-2");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest("user-r1", NotificationTrigger.Chat, "hi", "body"), default);
        await service.SendAsync(
            new PushNotificationRequest("user-r2", NotificationTrigger.NewOffer, "offer", "body"), default);

        var tracker = factory.Services.GetRequiredService<IPushDeliveryTracker>();
        var recent = await tracker.GetRecentAsync(10, default);
        recent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Push_Inherits_Persisted_Language_From_User_Profile()
    {
        // T-backend-029 AC #6: when callers don't supply a Language, the
        // unified service stamps the request with the recipient's persisted
        // language so transports receive a locale-tagged payload.
        var factory = NewFactory(out _);
        var users = factory.Services.GetRequiredService<InMemoryUsersStore>();
        var now = DateTimeOffset.UtcNow;
        users.Seed(new UserProfile
        {
            Id = "user-lang",
            Phone = "+96550008888",
            Name = "Nora",
            Language = "ar",
            Roles = new List<string> { "customer" },
            CreatedAt = now,
            UpdatedAt = now
        });
        await RegisterDevice(factory, "user-lang", DevicePlatform.Fcm, "tok-lang");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        var result = await service.SendAsync(
            new PushNotificationRequest("user-lang", NotificationTrigger.Chat, "مرحبا", "نص"),
            CancellationToken.None);

        result.Outcome.Should().Be(PushDeliveryOutcome.Delivered);

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.Sent.Single().Request.Language.Should().Be("ar");
    }

    [Fact]
    public async Task Caller_Provided_Language_Takes_Precedence_Over_Profile()
    {
        // When a caller already rendered the payload in a specific locale,
        // the service must not silently override it with the profile value.
        var factory = NewFactory(out _);
        var users = factory.Services.GetRequiredService<InMemoryUsersStore>();
        var now = DateTimeOffset.UtcNow;
        users.Seed(new UserProfile
        {
            Id = "user-lang-override",
            Phone = "+96550008889",
            Name = "Sara",
            Language = "ar",
            Roles = new List<string> { "customer" },
            CreatedAt = now,
            UpdatedAt = now
        });
        await RegisterDevice(factory, "user-lang-override", DevicePlatform.Fcm, "tok-ov");

        var service = factory.Services.GetRequiredService<IPushNotificationService>();
        await service.SendAsync(
            new PushNotificationRequest(
                "user-lang-override", NotificationTrigger.Chat, "Hello", "Hi", Language: "en-US"),
            CancellationToken.None);

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.Sent.Single().Request.Language.Should().Be("en-US");
    }

    [Fact]
    public async Task Idempotent_Device_Registration_Does_Not_Duplicate()
    {
        var factory = NewFactory(out _);
        var store = factory.Services.GetRequiredService<IDeviceTokenStore>();

        await store.RegisterAsync(new DeviceToken("user-dup", DevicePlatform.Fcm, "same-tok"), default);
        await store.RegisterAsync(new DeviceToken("user-dup", DevicePlatform.Fcm, "same-tok"), default);

        var devices = await store.GetForUserAsync("user-dup", default);
        devices.Should().ContainSingle("idempotent registration deduplicates (token, platform) pair");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(out FakeClock clock)
    {
        var theClock = new FakeClock(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
        clock = theClock;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(theClock);
            });
        });
    }

    private static async Task RegisterDevice(
        WebApplicationFactory<Program> factory,
        string userId,
        DevicePlatform platform,
        string token)
    {
        var store = factory.Services.GetRequiredService<IDeviceTokenStore>();
        await store.RegisterAsync(new DeviceToken(userId, platform, token), default);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
