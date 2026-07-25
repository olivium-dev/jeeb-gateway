using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Requests;

/// <summary>
/// P7 — T1: ARITHMETIC PARITY between the read-side offer-wait projection and the
/// commit-side sweeper.
///
/// <para>This is the test that REPLACES a stored <c>gw_expires_at</c> column's
/// guarantee. The offer deadline is derived, not stored, so nothing but a test can
/// stop the two sides drifting: it must be impossible to change one arithmetic
/// without reddening the other.</para>
///
/// <para><b>Anti-cheat (T1.9):</b> the parity half drives the REAL
/// <see cref="RequestExpirySweeper.SweepOnceAsync"/> against a real
/// <see cref="InMemoryRequestsStore"/>. It deliberately does NOT re-call
/// <see cref="RequestExpiryMath"/> to compute its expectation — a test that calls the
/// same function twice proves nothing. The expectation comes from the PROJECTOR side
/// (<see cref="RequestExpiryMath.RemainingSeconds"/> == 0, which is what a client is
/// shown) and is asserted against the row status the sweeper actually committed.</para>
/// </summary>
public class RequestExpiryMathParityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    // Local catalog TTLs (InMemoryTiersStore seed).
    private static readonly TimeSpan UrgentTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SameDayTtl = TimeSpan.FromHours(2);
    private static readonly TimeSpan ScheduledTtl = TimeSpan.FromHours(24);

    // ── T1.1 / T1.2 — urgent, and the inclusive boundary ─────────────────────

    [Fact]
    public async Task T1_1_Urgent_At_TMinus1m_Reads_60s_And_Is_Not_Expired()
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("urgent", RequestStatus.Pending);
        var now = T0 + TimeSpan.FromMinutes(29);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        deadline.Should().Be(T0 + UrgentTtl);
        RequestExpiryMath.RemainingSeconds(deadline, now).Should().Be(60);
        RequestExpiryMath.IsExpiredAt(row, ttls, windows, now).Should().BeFalse();
    }

    [Fact]
    public async Task T1_2_Urgent_At_The_Deadline_Reads_Zero_And_Is_Expired_Inclusive()
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("urgent", RequestStatus.Pending);
        var now = T0 + UrgentTtl;

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        RequestExpiryMath.RemainingSeconds(deadline, now).Should().Be(0);
        RequestExpiryMath.IsExpiredAt(row, ttls, windows, now).Should().BeTrue(
            "the boundary is inclusive (d <= now) — the instant the client reads 0 is the "
            + "instant the sweeper is willing to expire");
    }

    [Fact]
    public async Task T1_2b_Sub_Second_Remainder_Ceilings_To_One_Not_Zero()
    {
        // 0 is RESERVED to mean "the window has closed". A 0.4 s remainder must read 1.
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("urgent", RequestStatus.Pending);
        var now = T0 + UrgentTtl - TimeSpan.FromMilliseconds(400);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        RequestExpiryMath.RemainingSeconds(deadline, now).Should().Be(1);
        RequestExpiryMath.IsExpiredAt(row, ttls, windows, now).Should().BeFalse();
    }

    [Fact]
    public async Task T1_2c_Past_The_Deadline_Clamps_At_Zero_Never_Negative()
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("urgent", RequestStatus.Pending);
        var now = T0 + UrgentTtl + TimeSpan.FromMinutes(17);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        RequestExpiryMath.RemainingSeconds(deadline, now).Should().Be(0);
    }

    // ── T1.3 / T1.4 — the other two catalog tiers ────────────────────────────

    [Theory]
    [InlineData("same-day", 2 * 60 * 60)]   // T1.3
    [InlineData("scheduled", 24 * 60 * 60)] // T1.4
    public async Task T1_3_And_T1_4_Catalog_Tiers_Resolve_Their_Own_Ttl(string tierId, int ttlSeconds)
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row(tierId, RequestStatus.Pending);
        var ttl = TimeSpan.FromSeconds(ttlSeconds);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        deadline.Should().Be(T0 + ttl);
        RequestExpiryMath.RemainingSeconds(deadline, T0).Should().Be(ttlSeconds);
        RequestExpiryMath.IsExpiredAt(row, ttls, windows, T0 + ttl - TimeSpan.FromSeconds(1))
            .Should().BeFalse();
        RequestExpiryMath.IsExpiredAt(row, ttls, windows, T0 + ttl).Should().BeTrue();
    }

    // ── T1.5 — legacy code canonicalisation ─────────────────────────────────

    [Fact]
    public async Task T1_5_Legacy_Code_Flash_Resolves_To_Urgent_Ttl_Not_The_Safe_Fallback()
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("flash", RequestStatus.Pending);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        deadline.Should().Be(T0 + UrgentTtl,
            "LegacyTierCodes.Canonicalize maps flash -> urgent; a legacy row must NOT silently "
            + "inherit the 24h safe fallback");
        deadline.Should().NotBe(T0 + TierExpiryWindowResolver.SafeExpiryWindow);
    }

    [Fact]
    public async Task T1_5b_Legacy_Code_Standard_Resolves_To_SameDay_Ttl()
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("standard", RequestStatus.Pending);

        RequestExpiryMath.DeadlineFor(row, ttls, windows).Should().Be(T0 + SameDayTtl);
    }

    // ── T1.6 / T1.7 — the two unknown-tier fallbacks ─────────────────────────

    [Fact]
    public async Task T1_6_Unknown_Tier_With_Populated_Catalog_Falls_Back_To_Default_Expiry_Tier()
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("no-such-tier", RequestStatus.Pending);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        deadline.Should().Be(T0 + ScheduledTtl,
            "the fallback chain is canonical id -> InMemoryTiersStore.DefaultExpiryTierId "
            + "('scheduled', 24h)");
        ttls.Should().ContainKey(InMemoryTiersStore.DefaultExpiryTierId);
    }

    [Fact]
    public async Task T1_7_Unknown_Tier_With_Empty_Catalog_Falls_Back_To_SafeExpiryWindow()
    {
        var (ttls, windows) = await EmptyCatalogAsync();
        var row = Row("no-such-tier", RequestStatus.Pending);

        ttls.Should().BeEmpty();
        RequestExpiryMath.DeadlineFor(row, ttls, windows)
            .Should().Be(T0 + TierExpiryWindowResolver.SafeExpiryWindow);
    }

    // ── T1.8 — no countdown applies outside the offer-wait window ────────────

    [Theory]
    [InlineData(RequestStatus.Accepted)]
    [InlineData(RequestStatus.PickedUp)]
    [InlineData(RequestStatus.Delivered)]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Expired)]
    [InlineData(RequestStatus.Scheduled)]
    public async Task T1_8_Non_PreAcceptance_Statuses_Have_No_Deadline_And_No_Remaining(string status)
    {
        var (ttls, windows) = await LocalCatalogAsync();
        var row = Row("urgent", status);

        var deadline = RequestExpiryMath.DeadlineFor(row, ttls, windows);

        deadline.Should().BeNull();
        RequestExpiryMath.RemainingSeconds(deadline, T0 + TimeSpan.FromDays(30)).Should().BeNull(
            "RemainingSeconds is null EXACTLY when the deadline is null");
        RequestExpiryMath.IsExpiredAt(row, ttls, windows, T0 + TimeSpan.FromDays(30))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(RequestStatus.Pending)]
    [InlineData(RequestStatus.Matched)]
    public async Task T1_8b_Both_PreAcceptance_Statuses_Do_Get_A_Deadline(string status)
    {
        var (ttls, windows) = await LocalCatalogAsync();

        RequestExpiryMath.DeadlineFor(Row("urgent", status), ttls, windows)
            .Should().Be(T0 + UrgentTtl,
                "matched is in RequestStatus.PreAcceptanceStates — the auction is still open");
    }

    // ── T1.9 — THE PARITY ASSERTION (drives the real sweeper) ────────────────

    /// <summary>
    /// T1.9 for the catalog + legacy + unknown-tier cases: for each row, at
    /// <c>now = deadline</c>, run the REAL <see cref="RequestExpirySweeper.SweepOnceAsync"/>
    /// and assert the row transitions to <c>expired</c> IFF the projector's
    /// <c>RemainingSeconds</c> reads 0. There must be no case where the client sees
    /// <c>&gt; 0</c> while the sweeper expires, or vice-versa.
    /// </summary>
    [Theory]
    [InlineData("urgent", 30 * 60)]
    [InlineData("same-day", 2 * 60 * 60)]
    [InlineData("scheduled", 24 * 60 * 60)]
    [InlineData("flash", 30 * 60)]           // legacy -> urgent
    [InlineData("standard", 2 * 60 * 60)]    // legacy -> same-day
    [InlineData("eco", 24 * 60 * 60)]        // legacy -> scheduled
    [InlineData("no-such-tier", 24 * 60 * 60)] // unknown -> DefaultExpiryTierId
    public async Task T1_9_Projector_Zero_And_Sweeper_Expiry_Agree_Exactly(string tierId, int ttlSeconds)
    {
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var clock = new FakeClock(T0);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var (ttls, windows) = await LocalCatalogAsync();
        var sweeper = CreateSweeper(services, clock, windows);

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = $"parity-{tierId}",
            Description = "parity row",
            TierId = tierId,
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);

        // (a) ONE SECOND BEFORE the deadline: the client is shown > 0, so the sweeper
        //     must leave the row pending.
        clock.Set(T0 + ttl - TimeSpan.FromSeconds(1));
        var beforeRow = (await store.GetAsync(created.Id, CancellationToken.None))!;
        var remainingBefore = RequestExpiryMath.RemainingSeconds(
            RequestExpiryMath.DeadlineFor(beforeRow, ttls, windows), clock.GetUtcNow());
        remainingBefore.Should().BeGreaterThan(0);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(created.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending,
                "the client is still shown {0}s — the sweeper must not have expired this row",
                remainingBefore);

        // (b) AT the deadline: the client is shown 0, so the sweeper MUST expire.
        clock.Set(T0 + ttl);
        var atRow = (await store.GetAsync(created.Id, CancellationToken.None))!;
        var remainingAt = RequestExpiryMath.RemainingSeconds(
            RequestExpiryMath.DeadlineFor(atRow, ttls, windows), clock.GetUtcNow());
        remainingAt.Should().Be(0);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(created.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired,
                "the client was shown 0:00 — the sweeper MUST have been willing to expire the same row");
    }

    /// <summary>
    /// T1.9, T1.8 half: a row the projector says has NO countdown (accepted) is never
    /// touched by the sweeper, however far the clock runs.
    /// </summary>
    [Fact]
    public async Task T1_9b_Row_With_No_Countdown_Is_Never_Expired_By_The_Sweeper()
    {
        var clock = new FakeClock(T0);
        using var services = BuildSweeperServices(clock);
        var store = services.GetRequiredService<IRequestsStore>();
        var (ttls, windows) = await LocalCatalogAsync();
        var sweeper = CreateSweeper(services, clock, windows);

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "parity-accepted",
            Description = "accepted row",
            TierId = "urgent",
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);
        (await store.SetStatusAsync(created.Id, RequestStatus.Accepted, CancellationToken.None))
            .Should().BeTrue();

        clock.Set(T0 + TimeSpan.FromDays(7));
        var row = (await store.GetAsync(created.Id, CancellationToken.None))!;
        RequestExpiryMath.RemainingSeconds(
            RequestExpiryMath.DeadlineFor(row, ttls, windows), clock.GetUtcNow())
            .Should().BeNull("no countdown applies to an accepted row");

        await sweeper.SweepOnceAsync(CancellationToken.None);

        (await store.GetAsync(created.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Accepted);
    }

    /// <summary>
    /// T1.7 parity: with an EMPTY tier catalog the projector falls back to
    /// <see cref="TierExpiryWindowResolver.SafeExpiryWindow"/> — and the sweeper must
    /// act on exactly that same instant, not on some other prefilter-derived one.
    /// </summary>
    [Fact]
    public async Task T1_9c_Empty_Catalog_SafeExpiryWindow_Is_Also_What_The_Sweeper_Acts_On()
    {
        var clock = new FakeClock(T0);
        using var services = BuildSweeperServices(clock, emptyCatalog: true);
        var store = services.GetRequiredService<IRequestsStore>();
        var (ttls, windows) = await EmptyCatalogAsync();
        var sweeper = CreateSweeper(services, clock, windows);

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "parity-empty-catalog",
            Description = "no catalog row",
            TierId = "no-such-tier",
            PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
            DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
        }, CancellationToken.None);

        clock.Set(T0 + TierExpiryWindowResolver.SafeExpiryWindow - TimeSpan.FromSeconds(1));
        await sweeper.SweepOnceAsync(CancellationToken.None);
        (await store.GetAsync(created.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending);

        clock.Set(T0 + TierExpiryWindowResolver.SafeExpiryWindow);
        var row = (await store.GetAsync(created.Id, CancellationToken.None))!;
        RequestExpiryMath.RemainingSeconds(
            RequestExpiryMath.DeadlineFor(row, ttls, windows), clock.GetUtcNow()).Should().Be(0);

        await sweeper.SweepOnceAsync(CancellationToken.None);
        (await store.GetAsync(created.Id, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Expired);
    }

    // ── the degradable upstream overlay (G-B) ────────────────────────────────

    [Fact]
    public async Task LoadTierTtls_Tolerates_A_Faulted_Upstream_And_Serves_The_Local_Catalog()
    {
        var windows = new TierExpiryWindowResolver(
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            ThrowingDelivery(),
            new QuietLogger<TierExpiryWindowResolver>());

        var ttls = await windows.LoadTierTtlsAsync(
            new InMemoryTiersStore(), CancellationToken.None, tolerateUpstreamFailure: true);

        ttls.Should().ContainKey("urgent").WhoseValue.Should().Be(UrgentTtl,
            "a delivery-service blip must degrade to the LOCAL catalog, never 5xx a read");
    }

    [Fact]
    public async Task LoadTierTtls_Still_Throws_For_The_Sweeper_Which_Does_Not_Tolerate()
    {
        var windows = new TierExpiryWindowResolver(
            new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = true }),
            ThrowingDelivery(),
            new QuietLogger<TierExpiryWindowResolver>());

        var act = async () => await windows.LoadTierTtlsAsync(
            new InMemoryTiersStore(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>(
            "the commit side keeps the default tolerateUpstreamFailure:false");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DeliveryRequest Row(string tierId, string status) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ClientId = "parity-client",
        Description = "parity row",
        Status = status,
        TierId = tierId,
        CreatedAt = T0,
    };

    private static async Task<(IReadOnlyDictionary<string, TimeSpan> Ttls, TierExpiryWindowResolver Windows)>
        LocalCatalogAsync()
    {
        var windows = LocalWindows();
        var ttls = await windows.LoadTierTtlsAsync(new InMemoryTiersStore(), CancellationToken.None);
        return (ttls, windows);
    }

    private static async Task<(IReadOnlyDictionary<string, TimeSpan> Ttls, TierExpiryWindowResolver Windows)>
        EmptyCatalogAsync()
    {
        var windows = LocalWindows();
        var ttls = await windows.LoadTierTtlsAsync(new EmptyTiersStore(), CancellationToken.None);
        return (ttls, windows);
    }

    /// <summary>Upstream OFF — the local catalog is the whole source, no HTTP involved.</summary>
    private static TierExpiryWindowResolver LocalWindows() => new(
        new StaticFlagsMonitor(new UpstreamFeatureFlags { Delivery = false }),
        ThrowingDelivery(),
        new QuietLogger<TierExpiryWindowResolver>());

    /// <summary>
    /// A real <see cref="DeliveryServiceClient"/> whose transport always faults — the
    /// "delivery-service blip" the read path must degrade through.
    /// </summary>
    private static IDeliveryServiceClient ThrowingDelivery() =>
        new DeliveryServiceClient(new HttpClient(new AlwaysFaultingHandler())
        {
            BaseAddress = new Uri("http://upstream-delivery.test/"),
        });

    private sealed class AlwaysFaultingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private static ServiceProvider BuildSweeperServices(FakeClock clock, bool emptyCatalog = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<InMemoryRequestsStore>();
        services.AddSingleton<IRequestsStore>(sp => sp.GetRequiredService<InMemoryRequestsStore>());
        services.AddSingleton<IDurableRequestsMirror, LocalExpiryAuthority>();
        services.AddSingleton<InMemoryRequestExpiryNotifier>();
        services.AddSingleton<IRequestExpiryNotifier>(sp =>
            sp.GetRequiredService<InMemoryRequestExpiryNotifier>());
        services.AddSingleton<JeebGateway.Availability.InMemoryPendingOffersStore>();
        services.AddSingleton<JeebGateway.Availability.IPendingOffersStore>(sp =>
            sp.GetRequiredService<JeebGateway.Availability.InMemoryPendingOffersStore>());
        if (emptyCatalog)
        {
            services.AddSingleton<JeebGateway.Tiers.ITiersStore, EmptyTiersStore>();
        }
        else
        {
            services.AddSingleton<JeebGateway.Tiers.ITiersStore, InMemoryTiersStore>();
        }

        return services.BuildServiceProvider();
    }

    private static RequestExpirySweeper CreateSweeper(
        IServiceProvider services,
        FakeClock clock,
        TierExpiryWindowResolver windows) =>
        new(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            windows,
            new StaticSourceMonitor(new RequestExpirySourceOptions { Source = "gateway" }),
            new QuietLogger<RequestExpirySweeper>());

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset at) => _now = at;
    }

    private sealed class QuietLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class StaticFlagsMonitor : IOptionsMonitor<UpstreamFeatureFlags>
    {
        public StaticFlagsMonitor(UpstreamFeatureFlags value) => CurrentValue = value;
        public UpstreamFeatureFlags CurrentValue { get; }
        public UpstreamFeatureFlags Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<UpstreamFeatureFlags, string?> listener) => null;
    }

    private sealed class StaticSourceMonitor : IOptionsMonitor<RequestExpirySourceOptions>
    {
        public StaticSourceMonitor(RequestExpirySourceOptions value) => CurrentValue = value;
        public RequestExpirySourceOptions CurrentValue { get; }
        public RequestExpirySourceOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RequestExpirySourceOptions, string?> listener) => null;
    }

    /// <summary>A catalog with no tiers at all — the T1.7 SafeExpiryWindow path.</summary>
    private sealed class EmptyTiersStore : JeebGateway.Tiers.ITiersStore
    {
        public Task<IReadOnlyList<DeliveryTier>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliveryTier>>(Array.Empty<DeliveryTier>());

        public Task<DeliveryTier?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult<DeliveryTier?>(null);

        public Task<DeliveryTier> CreateAsync(DeliveryTierCreate input, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DeliveryTier?> ReplaceAsync(string id, DeliveryTierReplace input, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>
    /// Fast local expiry authority (mirrors <c>RequestExpirySweeperTests.LocalExpiryAuthority</c>):
    /// only pre-acceptance rows win the transition, exactly once. The real cross-replica
    /// guarantees live in PostgresRequestExpiryAuthorityTests.
    /// </summary>
    private sealed class LocalExpiryAuthority : IDurableRequestsMirror
    {
        private readonly InMemoryRequestsStore _rows;
        private readonly HashSet<string> _expired = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public LocalExpiryAuthority(InMemoryRequestsStore rows) => _rows = rows;

        public async Task<bool> MarkExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct)
        {
            var row = await _rows.GetAsync(requestId, ct);
            if (row is null || !RequestStatus.IsPreAcceptance(row.Status)) return false;
            lock (_lock) { return _expired.Add(requestId); }
        }

        public Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct) => Task.CompletedTask;

        public Task MarkCancelledAsync(
            string requestId, string gwStatus, string? cancelledBy, string? cancellationReason,
            DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateLifecycleAsync(
            string requestId, string? gwStatus, string? gwJeeberId, decimal? gwAcceptedFee,
            DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());

        public Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());

        public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct) =>
            Task.FromResult<DeliveryRequest?>(null);
    }
}
