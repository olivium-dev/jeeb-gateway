using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Conversations.Client;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-335 (P45) — CRITICAL data-loss regression pins for the chat SEND path.
///
/// <para><b>The defect these pin.</b> The mobile client keys a chat send on
/// <c>msg-{conversationId}-{N}-u-{userId}</c> where <c>N</c> is a per-screen-session
/// counter that RESTARTS AT 0 on every chat mount. Re-entering a thread the user
/// already used re-presented keys the gateway had already stored, so
/// <see cref="IdempotencyMiddleware"/> replayed a cached <c>201</c> and NEVER
/// forwarded the message to chat-service. The sender saw success; the recipient
/// never received anything. Live-reproduced 2/2 on physical devices (index 0
/// dropped, index 1 dropped, index 2 landed).</para>
///
/// <para><b>Contract pinned here.</b>
/// <list type="number">
///   <item>The SAME logical send retried dedupes EXACTLY once (upstream called once,
///         replay carries <c>Idempotency-Replayed</c>).</item>
///   <item>Two DIFFERENT sends that collide on a re-used client counter are BOTH
///         forwarded upstream. <b>This is the regression that must never return.</b></item>
///   <item>A fresh chat mount re-using index 0 (and 1, 2 …) never replays a cached
///         201 — every message of the second mount reaches chat-service.</item>
///   <item>The key forwarded DOWNSTREAM to chat-service is de-collided too, so the
///         same defect cannot simply relocate one hop into chat-service's own
///         dedup.</item>
/// </list></para>
///
/// <para><b>Non-vacuity.</b> Every collision test first PROVES dedup is live on this
/// host (same key + identical body ⇒ one upstream call + <c>Idempotency-Replayed</c>)
/// before asserting that a colliding-but-different send is forwarded. A test cannot
/// pass by accident because the middleware was not mounted, the state-service store
/// was missing, or the endpoint 4xx'd: those all break the guard half first. See
/// <see cref="Vacuity_Guard_Dedup_Is_Actually_Live_On_This_Host"/>.</para>
/// </summary>
public sealed class ChatSendIdempotencyCollisionTests
{
    // ------------------------------------------------------------------
    // Vacuity guard — dedup must genuinely be ON, or nothing below means anything.
    // ------------------------------------------------------------------

    /// <summary>
    /// Explicit vacuity guard. If the idempotency middleware were not mounted (or the
    /// store were missing), the "both forwarded" assertions below would pass trivially.
    /// This test fails in exactly that situation: it demands a real replay.
    /// </summary>
    [Fact]
    public async Task Vacuity_Guard_Dedup_Is_Actually_Live_On_This_Host()
    {
        var fake = new CountingConversationClient();
        using var factory = MakeFactory(fake);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613350001");

        const string key = "msg-conv-vac-0-u-user-vac";

        var first = await Send(http, token, "conv-vac", key, "hello");
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains("Idempotency-Replayed").Should()
            .BeFalse("the first execution is not a replay");

        var replay = await Send(http, token, "conv-vac", key, "hello");
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.Headers.Contains("Idempotency-Replayed").Should()
            .BeTrue("the idempotency middleware MUST be live for the collision pins below to mean anything");
        fake.AppendCalls.Should().Be(1, "an identical retry must not reach chat-service twice");
    }

    // ------------------------------------------------------------------
    // (a) The SAME send retried is deduped exactly once.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Same_Send_Retried_Is_Deduped_Exactly_Once()
    {
        var fake = new CountingConversationClient();
        using var factory = MakeFactory(fake);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613350002");

        const string key = "msg-conv-a-3-u-user-a";

        var first = await Send(http, token, "conv-a", key, "on my way");
        var retryA = await Send(http, token, "conv-a", key, "on my way");
        var retryB = await Send(http, token, "conv-a", key, "on my way");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retryA.StatusCode.Should().Be(HttpStatusCode.Created);
        retryB.StatusCode.Should().Be(HttpStatusCode.Created);

        retryA.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        retryB.Headers.Contains("Idempotency-Replayed").Should().BeTrue();

        fake.AppendCalls.Should().Be(1,
            "retrying the SAME send (same key, same bytes) is the legitimate idempotency case "
            + "and must collapse onto exactly one chat-service append");
    }

    // ------------------------------------------------------------------
    // (b) THE REGRESSION: two DIFFERENT sends colliding on a client counter
    //     must BOTH be forwarded. This is the data-loss bug (JEBV4-335).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_Different_Sends_Colliding_On_A_Client_Counter_Are_Both_Forwarded()
    {
        var fake = new CountingConversationClient();
        using var factory = MakeFactory(fake);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613350003");

        // The EXACT collidable shape the mobile client mints. N restarts at 0 on
        // every chat mount, so this identical key is presented by two genuinely
        // different sends.
        const string collidingKey = "msg-conv-b-0-u-user-b";

        // --- non-vacuity: dedup is live on this host (identical retry replays) ---
        var probe = await Send(http, token, "conv-b", "msg-conv-b-probe-u-user-b", "probe");
        probe.StatusCode.Should().Be(HttpStatusCode.Created);
        var probeReplay = await Send(http, token, "conv-b", "msg-conv-b-probe-u-user-b", "probe");
        probeReplay.Headers.Contains("Idempotency-Replayed").Should()
            .BeTrue("dedup must be live, otherwise the assertions below are vacuous");
        fake.AppendCalls.Should().Be(1);

        // --- the regression proper ---
        var mountOne = await Send(http, token, "conv-b", collidingKey, "first mount message");
        var mountTwo = await Send(http, token, "conv-b", collidingKey, "second mount message");

        mountOne.StatusCode.Should().Be(HttpStatusCode.Created);
        mountTwo.StatusCode.Should().Be(HttpStatusCode.Created);

        mountTwo.Headers.Contains("Idempotency-Replayed").Should().BeFalse(
            "a DIFFERENT send that merely re-used a client counter must never be answered "
            + "with another send's cached 201 — that is the silent-drop defect");

        fake.AppendCalls.Should().Be(3,
            "probe + both colliding sends must each reach chat-service");
        fake.Bodies.Should().Contain("first mount message");
        fake.Bodies.Should().Contain("second mount message",
            "the second send is the message that was being silently dropped");

        // The DOWNSTREAM key must be de-collided too, otherwise chat-service's own
        // dedup would drop the second message one hop later.
        fake.ForwardedKeys.Should().OnlyHaveUniqueItems(
            "the gateway must not hand chat-service the raw collidable counter");
        fake.ForwardedKeys.Should().OnlyContain(
            k => k.StartsWith("msg-conv-b-", System.StringComparison.Ordinal),
            "the client key must remain a readable prefix of the forwarded key");
    }

    // ------------------------------------------------------------------
    // (c) A fresh chat mount re-using index 0 must not replay a cached 201.
    //     This is the live physical-device repro: index 0 dropped, 1 dropped,
    //     2 landed.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fresh_Chat_Mount_Reusing_Index_Zero_Does_Not_Replay_A_Cached_201()
    {
        var fake = new CountingConversationClient();
        using var factory = MakeFactory(fake);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613350004");

        const string conversationId = "conv-c";
        string KeyFor(int index) => $"msg-{conversationId}-{index}-u-{userId}";

        // Mount #1 — the per-screen counter runs 0,1,2.
        for (var i = 0; i < 3; i++)
        {
            var sent = await Send(http, token, conversationId, KeyFor(i), $"mount-1 message {i}");
            sent.StatusCode.Should().Be(HttpStatusCode.Created);
            sent.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
        }

        fake.AppendCalls.Should().Be(3);

        // --- non-vacuity: dedup is live (re-sending mount-1's index 1 VERBATIM replays) ---
        var verbatimRetry = await Send(http, token, conversationId, KeyFor(1), "mount-1 message 1");
        verbatimRetry.Headers.Contains("Idempotency-Replayed").Should()
            .BeTrue("dedup must be live, otherwise the re-mount assertions are vacuous");
        fake.AppendCalls.Should().Be(3, "a verbatim retry must not append again");

        // Mount #2 — the user leaves and re-enters the thread. The client counter
        // RESTARTS AT 0, so the same three keys are presented for three brand-new
        // messages. Every one of them must reach chat-service.
        for (var i = 0; i < 3; i++)
        {
            var sent = await Send(http, token, conversationId, KeyFor(i), $"mount-2 message {i}");
            sent.StatusCode.Should().Be(HttpStatusCode.Created);
            sent.Headers.Contains("Idempotency-Replayed").Should().BeFalse(
                $"mount-2 index {i} is a NEW message, not a retry of mount-1 index {i}");
        }

        fake.AppendCalls.Should().Be(6,
            "all three mount-2 messages must be forwarded — the live repro lost indexes 0 and 1");

        for (var i = 0; i < 3; i++)
        {
            fake.Bodies.Should().Contain($"mount-1 message {i}");
            fake.Bodies.Should().Contain($"mount-2 message {i}");
        }
    }

    // ------------------------------------------------------------------
    // Cross-participant collision (BUG-13's sibling): an OLD client that omits
    // the "-u-{userId}" scope must still not have its message eaten by the other
    // participant's Nth message.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Unscoped_Legacy_Key_Does_Not_Collide_Across_Participants()
    {
        var fake = new CountingConversationClient();
        using var factory = MakeFactory(fake);
        var http = factory.CreateClient();
        var (customerToken, _) = await MintSession(http, "+9613350005");
        var (jeeberToken, _) = await MintSession(http, "+9613350006");

        // Pre-BUG-13 shape: no "-u-{userId}" suffix at all.
        const string legacyKey = "msg-conv-d-0";

        var customer = await Send(http, customerToken, "conv-d", legacyKey, "customer says hi");
        var jeeber = await Send(http, jeeberToken, "conv-d", legacyKey, "jeeber says hi");

        customer.StatusCode.Should().Be(HttpStatusCode.Created);
        jeeber.StatusCode.Should().Be(HttpStatusCode.Created);
        jeeber.Headers.Contains("Idempotency-Replayed").Should().BeFalse();

        fake.AppendCalls.Should().Be(2);
        fake.Bodies.Should().Contain("customer says hi").And.Contain("jeeber says hi");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<HttpResponseMessage> Send(
        HttpClient http, string token, string conversationId, string idempotencyKey, string body)
    {
        var msg = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(new { kind = "text", body }),
        };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await http.SendAsync(msg);
    }

    private const string AppId = "jeeb-idem-collision-test-app";

    private static WebApplicationFactory<Program> MakeFactory(IJeebConversationClient fake) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Mount the gateway-wide idempotency middleware (it is gated on a
            // configured state-service) WITHOUT needing the real service.
            builder.UseSetting("JeebStateService:BaseUrl", "http://localhost:10073");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(IIdempotencyStore));
                services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();

                services.RemoveAll<IJeebConversationClient>();
                services.AddSingleton(fake);

                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtpClient());

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Chat = true;
                    f.Otp = true;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    private static async Task<(string Token, string UserId)> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        return (json["accessToken"]!.Value<string>()!, json["user"]!["userId"]!.Value<string>()!);
    }

    /// <summary>
    /// chat-service stand-in that COUNTS appends and records what the gateway
    /// forwarded, so "was this message actually forwarded?" is directly assertable.
    /// </summary>
    private sealed class CountingConversationClient : IJeebConversationClient
    {
        private readonly object _gate = new();
        private readonly List<string> _bodies = new();
        private readonly List<string> _forwardedKeys = new();

        public int AppendCalls { get; private set; }

        public IReadOnlyList<string> Bodies
        {
            get { lock (_gate) { return _bodies.ToArray(); } }
        }

        public IReadOnlyList<string> ForwardedKeys
        {
            get { lock (_gate) { return _forwardedKeys.ToArray(); } }
        }

        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
        {
            lock (_gate)
            {
                AppendCalls++;
                _bodies.Add(request.Body ?? string.Empty);
                if (request.IdempotencyKey is { Length: > 0 } key) _forwardedKeys.Add(key);
            }

            return Task.FromResult(new JeebMessageResponse
            {
                MessageId = $"srv-msg-{AppendCalls}",
                Kind = request.Kind,
                Subtype = request.Subtype,
                AuthorId = request.AuthorId,
                Audience = request.Audience,
                Payload = request.Payload,
                Body = request.Body,
            });
        }

        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = "conv-" + request.RequestId,
                CorrelationKey = request.RequestId,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = "conv-" + correlationKey,
                CorrelationKey = correlationKey,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });

        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(new JeebMessageListResponse
            {
                Messages = new List<JeebMessageResponse>(),
            });

        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => Task.FromResult(new JeebMessageListResponse
            {
                Messages = new List<JeebMessageResponse>(),
            });

        public Task<JeebConversationMembership> GetMembershipAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(new JeebConversationMembership { IsMember = true, RoleInConvo = "client" });

        public Task<JeebConversationParticipant> AddParticipantAsync(
            string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationParticipant
            {
                UserId = request.UserId,
                RoleInConvo = request.RoleInConvo,
                RemovedAt = null,
            });

        public Task<JeebConversationResponse> AdvancePhaseAsync(
            string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = conversationId,
                CorrelationKey = conversationId,
                Phase = request.Phase,
                Participants = new List<JeebConversationParticipant>(),
            });
    }

    private sealed class StubOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
