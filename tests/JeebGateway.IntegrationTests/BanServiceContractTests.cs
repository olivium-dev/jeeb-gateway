using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// thin-BFF wire (T-thin-bff-ban) contract-seam guard for ban-service
/// (Rust / Actix-Web, host port 10065). The rest of the suite stubs
/// <see cref="IBanServiceClient"/>, so the REAL JSON deserialization of
/// ban-service's <b>snake_case</b> bodies is never exercised. This suite drives:
///
/// <list type="bullet">
///   <item>the REAL <see cref="BanServiceClient"/> against a fake
///   <see cref="HttpMessageHandler"/> that returns the LITERAL ban-service
///   bodies — locking the JSON seam (user_id / ban_statuses / banned_until /
///   is_currently_banned / current_stage); and</item>
///   <item>the REAL <see cref="BanServiceJeeberRestrictionStore"/> over that
///   client — proving the restriction READ path (is-restricted / active-expiry)
///   and WRITE path (apply) that <see cref="CancellationService"/> consumes from
///   BOTH AdminCancellationsController and DeliveriesController.</item>
/// </list>
///
/// A third, OPT-IN test drives the LIVE service at 192.168.2.50:10065 when
/// <c>JEEB_BAN_LIVE=1</c>, proving the full read/write round-trip end-to-end.
/// </summary>
public class BanServiceContractTests
{
    private const string JeeberId = "6f1515fb-ace5-4868-bb80-0c4802c9300e";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-01T15:46:25Z");

    // -----------------------------------------------------------------
    // (1) JSON seam — BanServiceClient against literal ban-service bytes
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetStatus_Binds_SnakeCase_ActivePartialBan()
    {
        // The LITERAL ban-service body for GET /api/v1/ban/{id}/status with an
        // active PARTIAL_BAN (verified against the live service 2026-06-01).
        var client = ClientReturning(HttpStatusCode.OK,
            $$"""
            {"user_id":"{{JeeberId}}","ban_statuses":[{"user_id":"{{JeeberId}}","ban_type":"yellow","current_stage":2,"status":"PARTIAL_BAN","message":"banned 1h","banned_until":"2026-06-01T16:46:25.755001353Z","last_updated":"2026-06-01T15:46:25.755009683Z","is_currently_banned":true}]}
            """);

        var result = await client.GetStatusAsync(JeeberId, CancellationToken.None);

        result.UserId.Should().Be(JeeberId);
        result.BanStatuses.Should().ContainSingle();
        var s = result.BanStatuses[0];
        s.BanType.Should().Be("yellow");
        s.CurrentStage.Should().Be(2);
        s.Status.Should().Be("PARTIAL_BAN");
        s.IsCurrentlyBanned.Should().BeTrue();
        // ban-service emits 9 fractional digits (nanoseconds); DateTimeOffset
        // holds 100ns ticks (7 digits), so the wire value truncates. Assert with
        // a 1ms tolerance rather than exact ticks to lock the seam without
        // depending on sub-tick rounding behaviour.
        s.BannedUntil.Should().BeCloseTo(
            DateTimeOffset.Parse("2026-06-01T16:46:25.7550014Z"), TimeSpan.FromMilliseconds(1));

        result.IsCurrentlyBanned.Should().BeTrue();
        result.ActiveExpiry.Should().BeCloseTo(
            DateTimeOffset.Parse("2026-06-01T16:46:25.7550014Z"), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GetStatus_Binds_Empty_BanStatuses_As_Unrestricted()
    {
        // LITERAL body for a never-banned user (verified live).
        var client = ClientReturning(HttpStatusCode.OK,
            $$"""{"user_id":"{{JeeberId}}","ban_statuses":[]}""");

        var result = await client.GetStatusAsync(JeeberId, CancellationToken.None);

        result.BanStatuses.Should().BeEmpty();
        result.IsCurrentlyBanned.Should().BeFalse();
        result.ActiveExpiry.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_Warning_Stage_Reads_As_NotBanned_With_NullExpiry()
    {
        // LITERAL body for a yellow stage-1 WARNING (verified live): banned_until
        // null, is_currently_banned false.
        var client = ClientReturning(HttpStatusCode.OK,
            $$"""
            {"user_id":"{{JeeberId}}","ban_statuses":[{"user_id":"{{JeeberId}}","ban_type":"yellow","current_stage":1,"status":"WARNING","message":"warning","banned_until":null,"last_updated":"2026-06-01T15:46:25.728359951Z","is_currently_banned":false}]}
            """);

        var result = await client.GetStatusAsync(JeeberId, CancellationToken.None);

        result.IsCurrentlyBanned.Should().BeFalse();
        result.ActiveExpiry.Should().BeNull();
    }

    [Fact]
    public async Task ApplyBan_Posts_To_BanType_Route_And_Binds_Response()
    {
        var capture = new CapturingHandler(HttpStatusCode.OK,
            $$"""
            {"user_id":"{{JeeberId}}","ban_type":"yellow","current_stage":1,"status":"WARNING","message":"warn","banned_until":null,"last_updated":"2026-06-01T15:46:25Z","is_currently_banned":false}
            """);
        var client = new BanServiceClient(
            new HttpClient(capture) { BaseAddress = new Uri("http://ban.test/") });

        var result = await client.ApplyBanAsync(JeeberId, "yellow", CancellationToken.None);

        capture.LastMethod.Should().Be(HttpMethod.Post);
        capture.LastUri!.AbsolutePath.Should().Be($"/api/v1/ban/{JeeberId}/yellow");
        result.BanType.Should().Be("yellow");
        result.Status.Should().Be("WARNING");
    }

    // -----------------------------------------------------------------
    // (2) Restriction-store seam — BanServiceJeeberRestrictionStore over
    //     the real client, proving the read/write path CancellationService uses.
    // -----------------------------------------------------------------

    [Fact]
    public async Task RestrictionStore_IsRestricted_True_When_ActiveBan_Before_Expiry()
    {
        var store = StoreReturningStatus(
            $$"""
            {"user_id":"{{JeeberId}}","ban_statuses":[{"user_id":"{{JeeberId}}","ban_type":"yellow","current_stage":2,"status":"PARTIAL_BAN","message":"m","banned_until":"2026-06-01T16:46:25Z","last_updated":"2026-06-01T15:46:25Z","is_currently_banned":true}]}
            """);

        var restricted = await store.IsRestrictedAsync(JeeberId, Now, CancellationToken.None);
        var expiry = await store.GetActiveExpiryAsync(JeeberId, Now, CancellationToken.None);

        restricted.Should().BeTrue();
        expiry.Should().Be(DateTimeOffset.Parse("2026-06-01T16:46:25Z"));
    }

    [Fact]
    public async Task RestrictionStore_IsRestricted_False_When_BanExpiredBefore_At()
    {
        // is_currently_banned true but banned_until already passed relative to `at`
        // (ban-service not yet swept) → the gateway must read un-restricted.
        var store = StoreReturningStatus(
            $$"""
            {"user_id":"{{JeeberId}}","ban_statuses":[{"user_id":"{{JeeberId}}","ban_type":"yellow","current_stage":2,"status":"PARTIAL_BAN","message":"m","banned_until":"2026-06-01T15:00:00Z","last_updated":"2026-06-01T14:00:00Z","is_currently_banned":true}]}
            """);

        var restricted = await store.IsRestrictedAsync(JeeberId, Now, CancellationToken.None);
        var expiry = await store.GetActiveExpiryAsync(JeeberId, Now, CancellationToken.None);

        restricted.Should().BeFalse();
        expiry.Should().BeNull();
    }

    [Fact]
    public async Task RestrictionStore_IsRestricted_False_When_NoBan()
    {
        var store = StoreReturningStatus($$"""{"user_id":"{{JeeberId}}","ban_statuses":[]}""");

        (await store.IsRestrictedAsync(JeeberId, Now, CancellationToken.None)).Should().BeFalse();
        (await store.GetActiveExpiryAsync(JeeberId, Now, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RestrictionStore_Apply_Posts_Yellow_Progression()
    {
        var capture = new CapturingHandler(HttpStatusCode.OK,
            $$"""
            {"user_id":"{{JeeberId}}","ban_type":"yellow","current_stage":1,"status":"WARNING","message":"m","banned_until":null,"last_updated":"2026-06-01T15:46:25Z","is_currently_banned":false}
            """);
        var client = new BanServiceClient(
            new HttpClient(capture) { BaseAddress = new Uri("http://ban.test/") });
        var store = new BanServiceJeeberRestrictionStore(client);

        await store.ApplyAsync(JeeberId, Now, TimeSpan.FromHours(24), CancellationToken.None);

        capture.LastMethod.Should().Be(HttpMethod.Post);
        capture.LastUri!.AbsolutePath.Should().Be($"/api/v1/ban/{JeeberId}/yellow");
    }

    // -----------------------------------------------------------------
    // (3) OPT-IN real-wire test against the LIVE ban-service at
    //     192.168.2.50:10065. Runs only when JEEB_BAN_LIVE=1 so CI without
    //     VPN access stays green. Exercises apply (write) → status (read) →
    //     force-reset (cleanup) against the service's real Redis store.
    // -----------------------------------------------------------------

    [Fact]
    public async Task RealWire_RestrictionRoundTrip_Against_Live_Upstream()
    {
        if (Environment.GetEnvironmentVariable("JEEB_BAN_LIVE") != "1")
        {
            return; // opt-in only; not run in CI
        }

        var baseUrl = Environment.GetEnvironmentVariable("JEEB_BAN_BASEURL")
                      ?? "http://192.168.2.50:10065/";
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var client = new BanServiceClient(http);
        var store = new BanServiceJeeberRestrictionStore(client);

        var jeeberId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        try
        {
            // Initially unrestricted.
            (await store.IsRestrictedAsync(jeeberId, now, CancellationToken.None)).Should().BeFalse();

            // Apply twice: yellow stage 1 = WARNING (not banned), stage 2 =
            // PARTIAL_BAN (banned, with an expiry).
            await store.ApplyAsync(jeeberId, now, TimeSpan.FromHours(24), CancellationToken.None);
            await store.ApplyAsync(jeeberId, now, TimeSpan.FromHours(24), CancellationToken.None);

            var restricted = await store.IsRestrictedAsync(jeeberId, DateTimeOffset.UtcNow, CancellationToken.None);
            var expiry = await store.GetActiveExpiryAsync(jeeberId, DateTimeOffset.UtcNow, CancellationToken.None);

            restricted.Should().BeTrue("a yellow stage-2 PARTIAL_BAN should read as restricted");
            expiry.Should().NotBeNull("a PARTIAL_BAN carries a banned_until expiry");
            expiry!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
        }
        finally
        {
            // Cleanup: force-reset the test user so re-runs start clean.
            await http.PostAsync(
                $"api/v1/ban/{Uri.EscapeDataString(jeeberId)}/force-reset", null, CancellationToken.None);
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static BanServiceClient ClientReturning(HttpStatusCode status, string jsonBody)
        => new(new HttpClient(new CapturingHandler(status, jsonBody)) { BaseAddress = new Uri("http://ban.test/") });

    private static BanServiceJeeberRestrictionStore StoreReturningStatus(string jsonBody)
        => new(ClientReturning(HttpStatusCode.OK, jsonBody));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _jsonBody;

        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }

        public CapturingHandler(HttpStatusCode status, string jsonBody)
        {
            _status = status;
            _jsonBody = jsonBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
