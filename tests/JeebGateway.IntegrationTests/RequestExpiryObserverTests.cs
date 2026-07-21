using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class RequestExpiryObserverTests
{
    private static readonly DateTimeOffset ClockStart =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Observer_Is_A_NoOp_When_Source_Is_Gateway()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var request = await CreateRequestAsync(store, "gateway-authority-client");
        var delivery = new StubExpiredDeliveryClient([
            ExpiredRow(request, clock.GetUtcNow()),
        ]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            new StaticSourceMonitor(new RequestExpirySourceOptions { Source = "gateway" }),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending);
        delivery.Calls.Should().BeEmpty(
            "there must never be two TTL authorities active at once");
    }

    [Fact]
    public async Task Observer_Projects_Upstream_Expiry_Onto_Local_Status()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var request = await CreateRequestAsync(store, "projection-client");
        var expiredAt = clock.GetUtcNow() - TimeSpan.FromMinutes(3);
        var delivery = new StubExpiredDeliveryClient([
            ExpiredRow(request, expiredAt),
        ]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            DeliveryServiceSource(),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);

        var projected = await store.GetAsync(request.Id, CancellationToken.None);
        projected!.Status.Should().Be(RequestStatus.Expired);
        projected.ExpiredAt.Should().Be(expiredAt,
            "the gateway projects the upstream fact and computes no TTL");
    }

    [Fact]
    public async Task Observer_Notifies_The_Client_Exactly_Once_Per_Observed_Row()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var request = await CreateRequestAsync(store, "notification-client");
        var expiredAt = clock.GetUtcNow() - TimeSpan.FromMinutes(2);
        var delivery = new StubExpiredDeliveryClient([
            ExpiredRow(request, expiredAt),
        ]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            DeliveryServiceSource(),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);

        var notifier = services.GetRequiredService<InMemoryRequestExpiryNotifier>();
        notifier.Expiries.Should().ContainSingle()
            .Which.Should().Match<InMemoryRequestExpiryNotifier.ExpiryRecord>(expiry =>
                expiry.ClientId == request.ClientId
                && expiry.RequestId == request.Id
                && expiry.At == expiredAt);
    }

    [Fact]
    public async Task Overlapping_Poll_Window_Does_Not_Double_Project()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var request = await CreateRequestAsync(store, "overlap-client");
        var expiredAt = clock.GetUtcNow() - TimeSpan.FromMinutes(1);
        var delivery = new StubExpiredDeliveryClient([
            ExpiredRow(request, expiredAt),
        ]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            DeliveryServiceSource(),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);
        await observer.ObserveOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired);
        services.GetRequiredService<InMemoryRequestExpiryNotifier>()
            .Expiries.Should().ContainSingle(expiry => expiry.RequestId == request.Id,
                "TryExpireAsync is a no-op after the first terminal projection");
    }

    [Fact]
    public async Task Observer_Uses_An_Overlapping_Window_Twice_The_Interval()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var options = new RequestExpiryOptions
        {
            ObserverInterval = TimeSpan.FromSeconds(45),
            ObserverBatchLimit = 137,
        };
        var delivery = new StubExpiredDeliveryClient([]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(options),
            DeliveryServiceSource(),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);

        // The observer holds no cursor. This overlap absorbs clock skew and one missed tick.
        var call = delivery.Calls.Should().ContainSingle().Subject;
        call.Since.Should().Be(clock.GetUtcNow() - (2 * options.ObserverInterval));
        call.Limit.Should().Be(options.ObserverBatchLimit);
    }

    [Fact]
    public async Task SuppressNotifyBefore_Updates_State_But_Sends_No_Push()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var request = await CreateRequestAsync(store, "backfill-client");
        var expiredAt = clock.GetUtcNow() - TimeSpan.FromDays(5);
        var options = new RequestExpiryOptions
        {
            SuppressNotifyBefore = expiredAt + TimeSpan.FromMinutes(1),
        };
        var delivery = new StubExpiredDeliveryClient([
            ExpiredRow(request, expiredAt),
        ]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(options),
            DeliveryServiceSource(),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);

        // One-shot backfill lever: project the 89 historical stale rows without spamming real users.
        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired);
        services.GetRequiredService<InMemoryRequestExpiryNotifier>()
            .Expiries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Bad_Row_Does_Not_Abort_The_Pass()
    {
        var clock = new FakeClock(ClockStart);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var request = await CreateRequestAsync(store, "good-row-client");
        var expiredAt = clock.GetUtcNow() - TimeSpan.FromMinutes(4);
        var delivery = new StubExpiredDeliveryClient([
            new ExpiredDeliveryUpstream
            {
                DeliveryId = "   ",
                ClientId = "bad-row-client",
                ExpiredAt = expiredAt,
            },
            new ExpiredDeliveryUpstream
            {
                DeliveryId = "unknown-request-id",
                ClientId = "unknown-client",
                ExpiredAt = expiredAt,
            },
            ExpiredRow(request, expiredAt),
        ]);
        var observer = new RequestExpiryObserver(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            DeliveryServiceSource(),
            delivery,
            new RecordingLogger<RequestExpiryObserver>());

        await observer.ObserveOnceAsync(CancellationToken.None);

        (await store.GetAsync(request.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired);
    }

    private static StaticSourceMonitor DeliveryServiceSource() =>
        new(new RequestExpirySourceOptions { Source = "delivery-service" });

    private static ExpiredDeliveryUpstream ExpiredRow(
        DeliveryRequest request,
        DateTimeOffset expiredAt) =>
        new()
        {
            DeliveryId = request.Id,
            ClientId = request.ClientId,
            ExpiredAt = expiredAt,
        };

    private static Task<DeliveryRequest> CreateRequestAsync(
        IRequestsStore store,
        string clientId) =>
        store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "observer test request",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);

    private static ServiceProvider BuildSweeperServices(FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<InMemoryRequestsStore>();
        services.AddSingleton<IRequestsStore>(sp => sp.GetRequiredService<InMemoryRequestsStore>());
        services.AddSingleton<InMemoryRequestExpiryNotifier>();
        services.AddSingleton<IRequestExpiryNotifier>(sp =>
            sp.GetRequiredService<InMemoryRequestExpiryNotifier>());
        services.AddSingleton<JeebGateway.Availability.InMemoryPendingOffersStore>();
        services.AddSingleton<JeebGateway.Availability.IPendingOffersStore>(sp =>
            sp.GetRequiredService<JeebGateway.Availability.InMemoryPendingOffersStore>());
        services.AddSingleton<JeebGateway.Tiers.ITiersStore, InMemoryTiersStore>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public RecordingLogger(ConcurrentQueue<string>? messages = null) =>
            Messages = messages ?? new ConcurrentQueue<string>();

        public ConcurrentQueue<string> Messages { get; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class StaticSourceMonitor : IOptionsMonitor<RequestExpirySourceOptions>
    {
        public StaticSourceMonitor(RequestExpirySourceOptions value) => CurrentValue = value;

        public RequestExpirySourceOptions CurrentValue { get; }

        public RequestExpirySourceOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RequestExpirySourceOptions, string?> listener) => null;
    }

    private sealed class StubExpiredDeliveryClient
        : IDeliveryServiceClient, IDeliveryServiceClientDefaults
    {
        private readonly IReadOnlyList<ExpiredDeliveryUpstream> _rows;

        public StubExpiredDeliveryClient(IReadOnlyList<ExpiredDeliveryUpstream> rows) =>
            _rows = rows;

        public List<(DateTimeOffset Since, int Limit)> Calls { get; } = new();

        public Task<IReadOnlyList<ExpiredDeliveryUpstream>> ListExpiredDeliveriesAsync(
            DateTimeOffset since,
            int limit,
            CancellationToken ct)
        {
            Calls.Add((since, limit));
            return Task.FromResult(_rows);
        }
    }

    // IDeliveryServiceClient predates default interface members. Keep those unrelated
    // legacy calls out of the observer stub itself while still failing loudly if touched.
    private interface IDeliveryServiceClientDefaults : IDeliveryServiceClient
    {
        Task<IReadOnlyList<DeliveryTierDto>> IDeliveryServiceClient.ListTiersAsync(
            CancellationToken ct) => throw new NotSupportedException();

        Task<ShipmentsListDto> IDeliveryServiceClient.ListShipmentsAsync(
            string? orderId,
            string? stage,
            int? limit,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryRequestUpstream> IDeliveryServiceClient.CreateRequestAsync(
            CreateDeliveryRequestUpstream body,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryRowUpstream> IDeliveryServiceClient.CreateDeliveryRowAsync(
            CreateDeliveryRowUpstream body,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryRequestUpstream> IDeliveryServiceClient.GetDeliveryAsync(
            string deliveryId,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryOtpVerifyResult> IDeliveryServiceClient.VerifyOtpAsync(
            string deliveryId,
            string otpCode,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryTransitionUpstream> IDeliveryServiceClient.CanonicalTransitionAsync(
            string deliveryId,
            string to,
            string partySource,
            string actorId,
            string actorRole,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryReadUpstream?> IDeliveryServiceClient.GetCanonicalDeliveryAsync(
            string deliveryId,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryHandoverIssueResult> IDeliveryServiceClient.IssueHandoverOtpAsync(
            string deliveryId,
            string? codeHash,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryHandoverVerifyResult> IDeliveryServiceClient.VerifyHandoverOtpAsync(
            string deliveryId,
            bool success,
            string actorId,
            string actorRole,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryCancelResult> IDeliveryServiceClient.CancelDeliveryAsync(
            string deliveryId,
            DeliveryCancelUpstreamRequest body,
            CancellationToken ct) => throw new NotSupportedException();

        Task<JeeberAvailabilityUpstream> IDeliveryServiceClient.SetAvailabilityAsync(
            JeeberAvailabilityUpstreamRequest body,
            string jeeberId,
            CancellationToken ct) => throw new NotSupportedException();

        Task<JeeberAvailabilityUpstream?> IDeliveryServiceClient.GetAvailabilityAsync(
            string jeeberId,
            CancellationToken ct) => throw new NotSupportedException();

        Task<JeeberAvailabilityUpstream> IDeliveryServiceClient.HeartbeatAsync(
            string jeeberId,
            double lat,
            double lng,
            CancellationToken ct) => throw new NotSupportedException();

        Task<DeliveryMatchingRunResult> IDeliveryServiceClient.RunMatchingAsync(
            DeliveryMatchingRunRequest body,
            CancellationToken ct) => throw new NotSupportedException();

        Task<int> IDeliveryServiceClient.CountActiveDeliveriesByJeeberAsync(
            string jeeberId,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
