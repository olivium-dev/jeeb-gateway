using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Realtime;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Tracking;

/// <summary>
/// Continuous courier position — the gateway half.
///
/// <para>Three claims are pinned here.</para>
///
/// <para><b>1. The publish is genuinely fire-and-forget.</b> The claim is not "it
/// usually works"; it is that a realtime outage can neither fail nor slow
/// <c>POST /location/update</c>, which is the money path. So the upstream is driven into
/// its two worst states — throwing immediately, and never returning at all — and the
/// location write must still answer 200, promptly, with the fix durable in the store.
/// Injecting a fault into a <i>dependency</i> is not mocking the subject: the subject is
/// the gateway's isolation from that dependency, and the only way to observe isolation is
/// to break the thing it isolates you from. The un-faulted path is proved separately and
/// un-mocked against the live service (evidence/04, evidence/05).</para>
///
/// <para><b>2. The credential is scoped to one delivery.</b> Every token this gateway
/// hands out carries exactly one topic. The upstream's own
/// <c>POST /api/auth/token</c> defaults to <c>topics:["*"]</c> and is unauthenticated, so
/// "we scoped it" is a claim that has to be asserted on the actual bytes, not assumed.</para>
///
/// <para><b>3. Authorization is delivery-service's verdict, fail-closed.</b> A non-party
/// gets 403 before any credential is minted.</para>
///
/// <para><b>POSITIVE CONTROLS.</b> Every "it did not happen" assertion here is paired
/// with a case where the same probe DOES see it happen — a recorder that never records is
/// indistinguishable from a publisher that never publishes.</para>
/// </summary>
public class CourierPositionRealtimeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 56 bytes, matching the length class of a real Guardian secret, and >= the issuer's
    /// 32-byte floor. Test-only material; the deployed secret comes from the environment.
    /// </summary>
    private const string TestGuardianSecret = "test-only-guardian-secret-for-integration-tests-0123456789";

    private readonly WebApplicationFactory<Program> _bare;

    public CourierPositionRealtimeTests(WebApplicationFactory<Program> factory)
    {
        _bare = factory;
    }

    // ---------------------------------------------------------------------
    // 2. The credential carries exactly one topic.
    // ---------------------------------------------------------------------

    [Fact]
    public void Issued_Token_Is_Scoped_To_Exactly_One_Topic_And_One_Scope()
    {
        var issuer = NewIssuer(TestGuardianSecret);

        var token = issuer.Issue("customer-1", "jeeb:delivery:abc", RealtimeGuardianTokenIssuer.SubscribeOnly);

        token.Should().NotBeNull();
        var (header, payload) = Decode(token!.Token);

        // HS512 only: the upstream rejects HS256/HS384 outright (evidence/03).
        header.GetProperty("alg").GetString().Should().Be("HS512");

        // The two claims LiveComm.Policy.ACL reads. They MUST be JSON arrays — a
        // single-element claim flattened to a bare string makes Topic.matches_any?/2
        // (guarded on is_list/1) stop matching, and every publish 403s.
        payload.GetProperty("topics").ValueKind.Should().Be(JsonValueKind.Array);
        payload.GetProperty("scopes").ValueKind.Should().Be(JsonValueKind.Array);

        Strings(payload, "topics").Should().Equal(new[] { "jeeb:delivery:abc" });
        Strings(payload, "scopes").Should().Equal(new[] { "subscribe" },
            "a client credential must never carry publish");

        payload.GetProperty("sub").GetString().Should().Be("customer-1");
        payload.GetProperty("iss").GetString().Should().Be("live_comm");
        payload.GetProperty("aud").GetString().Should().Be("live_comm");
        payload.GetProperty("typ").GetString().Should().Be("access");
    }

    /// <summary>
    /// POSITIVE CONTROL for the assertion above: the same decoder, pointed at a token
    /// that carries the wildcard the upstream's open minter hands out, must SEE the
    /// wildcard. A scope assertion that cannot detect "*" proves nothing.
    /// </summary>
    [Fact]
    public void Wildcard_Topic_Is_Detectable_By_The_Same_Assertion()
    {
        var issuer = NewIssuer(TestGuardianSecret);
        var wildcard = issuer.Issue("anyone", "*", RealtimeGuardianTokenIssuer.SubscribeOnly)!;

        var (_, payload) = Decode(wildcard.Token);

        Strings(payload, "topics").Should().Equal(new[] { "*" },
            "the control must be able to see the very thing the real assertion forbids");
    }

    [Fact]
    public void Unconfigured_Issuer_Mints_Nothing_Rather_Than_Something_Unscoped()
    {
        var issuer = NewIssuer(secret: null);

        issuer.IsConfigured.Should().BeFalse();
        issuer.Issue("customer-1", "jeeb:delivery:abc", RealtimeGuardianTokenIssuer.SubscribeOnly)
            .Should().BeNull("fail closed — the alternative is degrading onto the upstream's open minter");
    }

    // ---------------------------------------------------------------------
    // 3. GET /v1/realtime/jeeb:delivery:{id} — authorization + descriptor.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Party_Gets_A_Descriptor_Whose_Token_Is_Scoped_To_That_Delivery()
    {
        using var factory = Factory(publishEnabled: false, guardianSecret: TestGuardianSecret);
        var (http, viewerId) = await SessionAsync(factory);
        var seed = await SeedDeliveryAsync(factory, clientId: viewerId);

        var resp = await http.GetAsync($"/v1/realtime/jeeb:delivery:{seed.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<DeliveryPositionChannelDescriptor>(JsonOptions);

        dto!.Topic.Should().Be($"jeeb:delivery:{seed.Id}");
        dto.Channel.Should().Be($"topic:jeeb:delivery:{seed.Id}");
        dto.Stream.Should().Be("location");
        dto.Token.Should().NotBeNullOrWhiteSpace();

        // The descriptor's own token, not merely the issuer in isolation, is delivery-scoped.
        var (_, payload) = Decode(dto.Token);
        Strings(payload, "topics").Should().Equal(new[] { $"jeeb:delivery:{seed.Id}" });
        Strings(payload, "scopes").Should().Equal(new[] { "subscribe" });
        payload.GetProperty("sub").GetString().Should().Be(viewerId);
    }

    [Fact]
    public async Task Non_Party_Is_Refused_Before_Any_Credential_Is_Minted()
    {
        using var factory = Factory(publishEnabled: false, guardianSecret: TestGuardianSecret);
        var (http, _) = await SessionAsync(factory);
        // The delivery belongs to somebody else entirely.
        var seed = await SeedDeliveryAsync(factory, clientId: $"other-client-{Guid.NewGuid()}");

        var resp = await http.GetAsync($"/v1/realtime/jeeb:delivery:{seed.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("eyJ",
            "a 403 must not leak a minted JWT in its body");
    }

    [Fact]
    public async Task Unknown_Delivery_Is_404()
    {
        using var factory = Factory(publishEnabled: false, guardianSecret: TestGuardianSecret);
        var (http, _) = await SessionAsync(factory);

        var resp = await http.GetAsync($"/v1/realtime/jeeb:delivery:{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delivery_Id_That_Would_Escape_The_Topic_Namespace_Is_Refused()
    {
        using var factory = Factory(publishEnabled: false, guardianSecret: TestGuardianSecret);
        var (http, _) = await SessionAsync(factory);

        // "*" is the ACL's wildcard. If it reached the topic builder unfiltered the
        // caller would be handed a credential for the entire bus.
        var resp = await http.GetAsync("/v1/realtime/jeeb:delivery:*");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_A_Guardian_Secret_The_Gate_503s_Instead_Of_Handing_Back_A_Useless_Descriptor()
    {
        using var factory = Factory(publishEnabled: false, guardianSecret: null);
        var (http, viewerId) = await SessionAsync(factory);
        var seed = await SeedDeliveryAsync(factory, clientId: viewerId);

        var resp = await http.GetAsync($"/v1/realtime/jeeb:delivery:{seed.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ---------------------------------------------------------------------
    // 1. Fire-and-forget.
    // ---------------------------------------------------------------------

    /// <summary>
    /// POSITIVE CONTROL for the two fault cases below. Without this, "the location write
    /// survived the outage" would be equally satisfied by a gateway that never publishes
    /// anything at all.
    /// </summary>
    [Fact]
    public async Task Accepted_Fix_Reaches_The_Realtime_Publish_With_The_Delivery_Topic_And_Location_Stream()
    {
        var recorder = new RecordingRealtimeClient();
        using var factory = Factory(publishEnabled: true, guardianSecret: TestGuardianSecret, realtime: recorder);
        var seed = await SeedDeliveryAsync(factory, RequestStatus.HeadingOff);

        var resp = await PostFixAsync(factory, seed.JeeberId, seed.Id, 24.7120, 46.6720);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var published = await recorder.WaitForOneAsync(TimeSpan.FromSeconds(10));
        published.Should().NotBeNull("the hosted CourierPositionPublisher must drain the queue");
        published!.Topic.Should().Be($"jeeb:delivery:{seed.Id}");
        published.Stream.Should().Be("location", "LiveComm.Throttle keys its 1 Hz policy on this stream name");
        published.Data["lat"].Should().Be(24.7120);
        published.Data["lng"].Should().Be(46.6720);
        published.Data["jeeberId"].Should().Be(seed.JeeberId);
    }

    /// <summary>NEGATIVE CONTROL: a fix with no delivery has no topic, so nothing is published.</summary>
    [Fact]
    public async Task Fix_Without_A_Delivery_Publishes_Nothing()
    {
        var recorder = new RecordingRealtimeClient();
        using var factory = Factory(publishEnabled: true, guardianSecret: TestGuardianSecret, realtime: recorder);

        var resp = await PostFixAsync(factory, $"jeeber-{Guid.NewGuid()}", deliveryId: null, 24.71, 46.67);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await recorder.WaitForOneAsync(TimeSpan.FromSeconds(2))).Should().BeNull();
    }

    [Fact]
    public async Task A_Realtime_Upstream_That_Throws_Cannot_Fail_The_Location_Write()
    {
        using var factory = Factory(
            publishEnabled: true, guardianSecret: TestGuardianSecret, realtime: new ThrowingRealtimeClient());
        var seed = await SeedDeliveryAsync(factory, RequestStatus.HeadingOff);

        var resp = await PostFixAsync(factory, seed.JeeberId, seed.Id, 24.7120, 46.6720);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions);
        body!.Accepted.Should().Be(1);

        // And the fix is durable, which is the whole point of the money path.
        var latest = await factory.Services.GetRequiredService<ILocationStore>()
            .GetLatestAsync(seed.JeeberId);
        latest!.Lat.Should().Be(24.7120);
    }

    [Fact]
    public async Task A_Realtime_Upstream_That_Never_Returns_Cannot_Slow_The_Location_Write()
    {
        using var blocker = new BlockingRealtimeClient();
        using var factory = Factory(
            publishEnabled: true, guardianSecret: TestGuardianSecret, realtime: blocker);
        var seed = await SeedDeliveryAsync(factory, RequestStatus.HeadingOff);

        var sw = Stopwatch.StartNew();
        var resp = await PostFixAsync(factory, seed.JeeberId, seed.Id, 24.7120, 46.6720);
        sw.Stop();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The publisher is wedged for the whole test; if the ingest path awaited it at
        // all, this would sit at the block's full duration instead of returning at once.
        blocker.Entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the publish must actually have been attempted — otherwise this proves nothing");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the location write must not wait on the realtime publish");

        // DISCRIMINATING CONTROL for the threshold itself. A "< 5 s" assertion is worth
        // nothing unless awaiting this same upstream would actually breach it — so await
        // it directly, on the same clock, and require that it does NOT come back in time.
        // If this ever completes, the blocker stopped blocking and the assertion above
        // has quietly become vacuous.
        var direct = blocker.PublishAsync(
            "jeeb:delivery:control", "location",
            new Dictionary<string, object?> { ["lat"] = 1.0 }, null, CancellationToken.None);
        (await Task.WhenAny(direct, Task.Delay(TimeSpan.FromSeconds(5)))).Should().NotBeSameAs(direct,
            "awaiting the wedged upstream must blow the very budget the location write met");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static RealtimeGuardianTokenIssuer NewIssuer(string? secret)
        => new(
            Options.Create(new RealtimeGuardianOptions { GuardianSecret = secret }),
            TimeProvider.System,
            NullLogger<RealtimeGuardianTokenIssuer>.Instance);

    private static IEnumerable<string?> Strings(JsonElement payload, string claim)
    {
        var value = payload.GetProperty(claim);
        // The claim MUST be a JSON array — a single-element claim flattened to a bare
        // string makes LiveComm's Topic.matches_any?/2 (guarded on is_list/1) stop
        // matching, and every publish silently 403s.
        value.ValueKind.Should().Be(JsonValueKind.Array, "claim '{0}' must serialize as an array", claim);
        return value.EnumerateArray().Select(e => e.GetString()).ToArray();
    }

    private static (JsonElement Header, JsonElement Payload) Decode(string jwt)
    {
        var parts = jwt.Split('.');
        parts.Should().HaveCount(3, "a JWS is header.payload.signature");
        return (Parse(parts[0]), Parse(parts[1]));

        static JsonElement Parse(string segment)
        {
            var s = segment.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return JsonDocument.Parse(Convert.FromBase64String(s)).RootElement.Clone();
        }
    }

    private WebApplicationFactory<Program> Factory(
        bool publishEnabled,
        string? guardianSecret,
        IRealtimeCommunicationClient? realtime = null)
        => _bare.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Realtime", "true");
            builder.UseSetting("Tracking:RealtimePublish:Enabled", publishEnabled ? "true" : "false");
            builder.UseSetting("Services:Realtime:GuardianSecret", guardianSecret ?? string.Empty);

            builder.ConfigureServices(services =>
            {
                // The GPS ingest path heartbeats delivery-service; the in-process presence
                // fake keeps that hop from needing a live Go upstream. Unrelated to the
                // realtime path under test (same swap LocationTrackingTests makes).
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());

                // The descriptor gate is [Authorize]d, so it needs a REAL session bearer
                // carrying sub == userId, not the X-User-Id dev header. Same arrangement
                // S08GatewayCloseoutTests uses for the chat realtime gate: a no-op OTP
                // upstream so /v1/auth/otp/verify mints one.
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubServiceOtpClient());
                services.Configure<JeebGateway.Services.UpstreamFeatureFlags>(f => f.Otp = true);
                services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test-app";
                    o.TtlSeconds = 300;
                });

                if (realtime is not null)
                {
                    services.RemoveAll<IRealtimeCommunicationClient>();
                    services.AddSingleton(realtime);
                }
            });
        });

    /// <summary>A real OTP-minted session: bearer + the user id its <c>sub</c> carries.</summary>
    private static async Task<(HttpClient Http, string UserId)> SessionAsync(
        WebApplicationFactory<Program> factory)
    {
        var bootstrap = factory.CreateClient();
        var phone = $"+9665{Random.Shared.NextInt64(10_000_000, 99_999_999)}";
        var resp = await bootstrap.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var token = json.GetProperty("accessToken").GetString()!;
        var userId = json.GetProperty("user").GetProperty("userId").GetString()!;

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (http, userId);
    }

    /// <summary>No-op OTP upstream so the verify path mints a real session (sub == userId).</summary>
    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        // ADR-005 §7 trusted-edge role declaration; dual-role satisfies both
        // delivery.track.own ({client}) and delivery.gps.stream ({jeeber}).
        c.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return c;
    }

    private static Task<HttpResponseMessage> PostFixAsync(
        WebApplicationFactory<Program> factory, string jeeberId, string? deliveryId, double lat, double lng)
        => AuthClient(factory, jeeberId).PostAsJsonAsync("/location/update", new
        {
            deliveryId,
            points = new object[]
            {
                new { lat, lng, accuracy = 6.5, timestamp = DateTimeOffset.UtcNow },
            }
        });

    private static async Task<Seed> SeedDeliveryAsync(
        WebApplicationFactory<Program> factory,
        string status = RequestStatus.PickedUp,
        string? clientId = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        clientId ??= $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            DropoffLocation = new GeoPoint { Lat = 24.8, Lng = 46.8 }
        }, default);
        await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        await store.SetStatusAsync(created.Id, status, default);

        return new Seed(created.Id, clientId, jeeberId);
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId);

    // --- fault-injected / recording upstreams -------------------------------------

    private sealed record Publication(
        string Topic, string Stream, IReadOnlyDictionary<string, object?> Data);

    private sealed class RecordingRealtimeClient : IRealtimeCommunicationClient
    {
        private readonly ConcurrentQueue<Publication> _seen = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task<RealtimePublishResult> PublishAsync(
            string topic, string stream, IReadOnlyDictionary<string, object?> data,
            IReadOnlyDictionary<string, object?>? meta, CancellationToken ct)
        {
            _seen.Enqueue(new Publication(topic, stream, data));
            _signal.Release();
            return Task.FromResult(new RealtimePublishResult { Ok = true, Id = "rec", Seq = 1 });
        }

        public Task<RealtimePublishResult> FanOutChatMessageAsync(
            string recipientId, IReadOnlyDictionary<string, object?> data, CancellationToken ct)
            => PublishAsync("jeeb:chat", $"user:{recipientId}", data, null, ct);

        public async Task<Publication?> WaitForOneAsync(TimeSpan timeout)
            => await _signal.WaitAsync(timeout) && _seen.TryDequeue(out var p) ? p : null;
    }

    private sealed class ThrowingRealtimeClient : IRealtimeCommunicationClient
    {
        public Task<RealtimePublishResult> PublishAsync(
            string topic, string stream, IReadOnlyDictionary<string, object?> data,
            IReadOnlyDictionary<string, object?>? meta, CancellationToken ct)
            => throw new RealtimePublishException(
                HttpStatusCode.ServiceUnavailable, "realtime is down");

        public Task<RealtimePublishResult> FanOutChatMessageAsync(
            string recipientId, IReadOnlyDictionary<string, object?> data, CancellationToken ct)
            => PublishAsync("jeeb:chat", $"user:{recipientId}", data, null, ct);
    }

    private sealed class BlockingRealtimeClient : IRealtimeCommunicationClient, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public async Task<RealtimePublishResult> PublishAsync(
            string topic, string stream, IReadOnlyDictionary<string, object?> data,
            IReadOnlyDictionary<string, object?>? meta, CancellationToken ct)
        {
            Entered.Set();
            await Task.Run(() => _release.Wait(TimeSpan.FromSeconds(30)), CancellationToken.None);
            return new RealtimePublishResult { Ok = true, Id = "blocked", Seq = 1 };
        }

        public Task<RealtimePublishResult> FanOutChatMessageAsync(
            string recipientId, IReadOnlyDictionary<string, object?> data, CancellationToken ct)
            => PublishAsync("jeeb:chat", $"user:{recipientId}", data, null, ct);

        public void Dispose()
        {
            _release.Set();
            Entered.Dispose();
            _release.Dispose();
        }
    }
}
