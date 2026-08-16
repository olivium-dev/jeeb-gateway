using System.Net;
using System.Text;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using JeebGateway.Users;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.UnitTests;

// OA-21 — durability register #9. The new-request fan-out used to take its audience
// from the in-process InMemoryAvailabilityStore; a restart emptied it, so every
// request afterwards pushed to NOBODY, silently and with no self-heal. The audience
// now comes from delivery-service (IPushAudienceSource).
//
// These are the two halves the defect needed and did not have:
//   * a non-empty audience MUST produce one push per recipient (an empty audience
//     that stays silent fails this), and
//   * an audience that cannot be READ must be loud and distinguishable from one
//     that is genuinely empty.
public class NewRequestPushAudienceTests
{
    private const string Initiator = "11111111-1111-1111-1111-111111111111";

    // Riyadh-ish pickup and two jeebers a few hundred metres away, well inside the
    // 50 km test radius, so the geo cut is not what decides these cases.
    private const double PickupLat = 24.7100;
    private const double PickupLng = 46.6750;

    private static NewRequestNotification Notification() => new(
        RequestId: "req-oa21",
        TierId: null,
        Description: "A parcel",
        InitiatorUserId: Initiator,
        PickupLat: PickupLat,
        PickupLng: PickupLng);

    [Fact]
    public async Task Every_available_jeeber_receives_one_per_user_push()
    {
        var audience = FakeAudience.Available(
            Jeeber("22222222-2222-2222-2222-222222222222"),
            Jeeber("33333333-3333-3333-3333-333333333333"));
        var (notifier, push, log) = Build(audience);

        await notifier.FanOutAsync(Notification(), CancellationToken.None);

        // THE register-#9 ASSERTION. With an empty audience — the restart defect —
        // no user path is dialled at all and this is what fails.
        Assert.Equal(
            new[]
            {
                "/api/v1/sent-payload/user/22222222-2222-2222-2222-222222222222",
                "/api/v1/sent-payload/user/33333333-3333-3333-3333-333333333333",
            },
            push.Paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(log.Lines, l => l.Message.Contains("audience-unavailable"));
    }

    [Fact]
    public async Task The_initiator_is_still_excluded_from_the_upstream_audience()
    {
        var audience = FakeAudience.Available(
            Jeeber(Initiator),
            Jeeber("44444444-4444-4444-4444-444444444444"));
        var (notifier, push, _) = Build(audience);

        await notifier.FanOutAsync(Notification(), CancellationToken.None);

        Assert.Equal(
            "/api/v1/sent-payload/user/44444444-4444-4444-4444-444444444444",
            Assert.Single(push.Paths));
    }

    [Fact]
    public async Task An_unreadable_audience_is_logged_at_error_with_a_distinguishable_marker()
    {
        var audience = FakeAudience.Broken(DeliveryServicePushAudience.AvailableRung);
        var (notifier, push, log) = Build(audience);

        await notifier.FanOutAsync(Notification(), CancellationToken.None);

        Assert.Empty(push.Paths);
        var error = Assert.Single(log.Lines.Where(l => l.Level == LogLevel.Error));
        Assert.Contains("audience-unavailable", error.Message);
        Assert.Contains(DeliveryServicePushAudience.AvailableRung, error.Message);
        // The operator must not be able to read this as "nobody was online".
        Assert.DoesNotContain("recipients=0", error.Message);
    }

    [Fact]
    public async Task The_fallback_rung_failing_is_reported_as_its_own_rung()
    {
        var audience = FakeAudience.BrokenFallback(DeliveryServicePushAudience.ReachableRung);
        var (notifier, push, log) = Build(audience);

        await notifier.FanOutAsync(Notification(), CancellationToken.None);

        Assert.Empty(push.Paths);
        var error = Assert.Single(log.Lines.Where(l => l.Level == LogLevel.Error));
        Assert.Contains("audience-unavailable", error.Message);
        Assert.Contains(DeliveryServicePushAudience.ReachableRung, error.Message);
    }

    // The control that separates "could not ask" from "asked, nobody there". Without
    // it the two error assertions above would pass on a notifier that shouted on
    // every silent fan-out, which is its own alarm-fatigue defect.
    [Fact]
    public async Task A_genuinely_empty_audience_is_a_warning_not_the_error_marker()
    {
        var audience = FakeAudience.Available();
        var (notifier, push, log) = Build(audience);

        await notifier.FanOutAsync(Notification(), CancellationToken.None);

        Assert.Empty(push.Paths);
        Assert.DoesNotContain(log.Lines, l => l.Message.Contains("audience-unavailable"));
        Assert.Contains(
            log.Lines,
            l => l.Level == LogLevel.Warning && l.Message.Contains("recipients=0"));
    }

    // ── DeliveryServicePushAudience: failure must never look like emptiness ──────

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_non_2xx_audience_read_throws_rather_than_returning_empty(
        HttpStatusCode status)
    {
        var source = AudienceOver(new StubUpstream(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"reason\":\"nope\"}", Encoding.UTF8, "application/json"),
        }));

        var ex = await Assert.ThrowsAsync<PushAudienceUnavailableException>(
            () => source.ListAvailableAsync(CancellationToken.None));

        Assert.Equal(DeliveryServicePushAudience.AvailableRung, ex.Rung);
    }

    [Fact]
    public async Task A_transport_failure_on_the_audience_read_throws()
    {
        var source = AudienceOver(
            new StubUpstream(_ => throw new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<PushAudienceUnavailableException>(
            () => source.ListAvailableAsync(CancellationToken.None));
    }

    // Anti-construction control: the same source DOES return rows when the owner
    // answers, so the throws above are the failure path and not a source that always
    // throws. Also pins the wire contract — envelope key, generic + alias id keys.
    [Fact]
    public async Task The_audience_source_binds_the_owners_envelope_and_maps_its_rows()
    {
        var upstream = new StubUpstream(_ => Json(
            """
            {"providers":[
              {"provider_id":"p-1","jeeber_id":"p-1","vehicle_type":"motorbike","lat":24.71,"lng":46.675},
              {"jeeber_id":"p-2","lat":1,"lng":2},
              {"lat":1,"lng":2}
            ],"count":3}
            """));
        var source = AudienceOver(upstream);

        var rows = await source.ListAvailableAsync(CancellationToken.None);

        // The third row has no usable id: dropped rather than pushed to "".
        Assert.Equal(new[] { "p-1", "p-2" }, rows.Select(r => r.UserId).ToArray());
        Assert.Equal(VehicleType.Motorbike, rows[0].VehicleType);
        Assert.Equal(24.71, rows[0].Latitude);
        Assert.Equal("/api/v1/providers/available", Assert.Single(upstream.Paths));
    }

    [Fact]
    public async Task The_fallback_read_dials_the_known_route_with_a_since_cutoff()
    {
        var upstream = new StubUpstream(_ => Json("""{"providers":[],"count":0}"""));
        var source = AudienceOver(upstream);

        await source.ListReachableSinceAsync(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal("/api/v1/providers/known", Assert.Single(upstream.Paths));
        Assert.Contains("since=", Assert.Single(upstream.Queries));
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private static JeeberAvailability Jeeber(string userId) => new()
    {
        UserId = userId,
        IsOnline = true,
        VehicleType = VehicleType.Car,
        Latitude = PickupLat,
        Longitude = PickupLng,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static (NewRequestPushNotifier Notifier, RecordingPushHandler Push, RecordingLogger Log)
        Build(IPushAudienceSource audience)
    {
        var handler = new RecordingPushHandler();
        var push = new ServicePushNotificationClient(
            "http://push.test/", new HttpClient(handler));
        var log = new RecordingLogger();
        var options = Options.Create(new NewRequestFanoutOptions
        {
            // Explicit radius so the D2 geo cut resolves without a tier catalog; the
            // fixtures all sit at the pickup point, so it never decides a case here.
            RadiusKm = 50,
        });

        var notifier = new NewRequestPushNotifier(
            push,
            new EmptyTierCatalog(),
            log,
            audience,
            new UnusedUsersStore(),
            new NewRequestFanoutQueue(8),
            options,
            TimeProvider.System);

        return (notifier, handler, log);
    }

    private sealed class FakeAudience : IPushAudienceSource
    {
        private readonly IReadOnlyList<JeeberAvailability>? _available;
        private readonly IReadOnlyList<JeeberAvailability>? _reachable;
        private readonly string? _failingRung;

        private FakeAudience(
            IReadOnlyList<JeeberAvailability>? available,
            IReadOnlyList<JeeberAvailability>? reachable,
            string? failingRung)
        {
            _available = available;
            _reachable = reachable;
            _failingRung = failingRung;
        }

        public static FakeAudience Available(params JeeberAvailability[] rows) =>
            new(rows, Array.Empty<JeeberAvailability>(), null);

        public static FakeAudience Broken(string rung) => new(null, null, rung);

        /// <summary>Rung 1 answers empty, so the fan-out falls through to rung 2, which fails.</summary>
        public static FakeAudience BrokenFallback(string rung) =>
            new(Array.Empty<JeeberAvailability>(), null, rung);

        public Task<IReadOnlyList<JeeberAvailability>> ListAvailableAsync(CancellationToken ct) =>
            _available is null
                ? Task.FromException<IReadOnlyList<JeeberAvailability>>(
                    new PushAudienceUnavailableException(_failingRung!, new HttpRequestException()))
                : Task.FromResult(_available);

        public Task<IReadOnlyList<JeeberAvailability>> ListReachableSinceAsync(
            DateTimeOffset since, CancellationToken ct) =>
            _reachable is null
                ? Task.FromException<IReadOnlyList<JeeberAvailability>>(
                    new PushAudienceUnavailableException(_failingRung!, new HttpRequestException()))
                : Task.FromResult(_reachable);
    }

    private sealed class RecordingPushHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            // The generated client treats 201, and only 201, as accepted.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record LogLine(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<NewRequestPushNotifier>
    {
        public List<LogLine> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(new LogLine(logLevel, formatter(state, exception)));
    }

    private sealed class EmptyTierCatalog : ITierCatalogResolver
    {
        public Task<TierCatalogSnapshot> SnapshotAsync(CancellationToken ct) =>
            Task.FromResult(TierCatalogSnapshot.Empty);

        public Task<DeliveryTier?> ResolveAsync(string? tierId, CancellationToken ct) =>
            Task.FromResult<DeliveryTier?>(null);
    }

    // The audience source is exercised over the REAL DeliveryServiceClient, so these
    // cases also pin the route it dials and the envelope it binds — not just the
    // mapping. Base address ends in /api/v1/ exactly as the DI registration builds it.
    private static DeliveryServicePushAudience AudienceOver(StubUpstream upstream) =>
        new(new DeliveryServiceClient(new HttpClient(upstream)
        {
            BaseAddress = new Uri("http://delivery.test/api/v1/"),
        }));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubUpstream : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubUpstream(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = respond;

        public List<string> Paths { get; } = new();

        public List<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            Queries.Add(request.RequestUri?.Query ?? string.Empty);
            return Task.FromResult(_respond(request));
        }
    }

    // The online rung must not need a profile lookup: every member throws, so a
    // regression that reintroduces one fails loudly instead of passing quietly.
    private sealed class UnusedUsersStore : IUsersStore
    {
        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct) => throw new NotSupportedException();
        public Task<UserProfile?> RevokeRoleAsync(string userId, string role, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> PurgePiiAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
    }
}
