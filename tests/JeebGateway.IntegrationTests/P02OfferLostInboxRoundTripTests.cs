using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.JeebNotifications;
using JeebGateway.Notifications;
using JeebGateway.service.ServiceNotification;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>P02 — the gateway's own offer_lost producer used to emit jeeb://offers/{offerId}, which
/// is outside the mobile route grammar, and the inbox read side 502'd the whole page over it.</summary>
public sealed class P02OfferLostInboxRoundTripTests
{
    private const string RequestId = "f0a1d3c2-4b56-4a78-9c01-2d3e4f5a6b7c";
    private const string OfferId = "9f8e7d6c-5b4a-4938-8271-6a5b4c3d2e1f";
    private const string LoserJeeberId = "b52eb018-3ece-44e9-856f-87f27ec32b7f";
    private const string StoredPreFixLink = "jeeb://offers/" + OfferId;

    [Fact]
    public async Task Producer_Emits_A_Link_Inside_The_Mobile_Route_Grammar()
    {
        var payload = await CaptureOfferLostPushPayloadAsync();

        var deepLink = payload.Value<string>("deepLink");
        deepLink.Should().Be(NotificationDeepLinkResolver.InboxRoot, "offer_lost has no route (PLAN-P02 §4)");
        NotificationDeepLinkResolver.MatchExplicitLink(deepLink!).Should().Be(deepLink,
            "an emitted link the read side cannot match is a link mobile cannot open");
        payload.Value<string>("requestId").Should().Be(RequestId, "the routing id is unchanged");
    }

    [Fact]
    public async Task Stored_PreFix_OfferLost_Row_Serves_The_Page_With_Inbox_Root_Semantics()
    {
        // D2 forbids a backfill, so the rows written before the producer fix stay as they are.
        var payload = await CaptureOfferLostPushPayloadAsync();
        payload["deepLink"] = StoredPreFixLink;

        var item = await SingleInboxItemAsync(payload);

        item.DeepLink.Should().Be(NotificationDeepLinkResolver.InboxRoot);
        item.Ref.Should().Be(RequestId, "the row still carries its request ref");
        item.Type.Should().Be("offer_lost");
    }

    [Fact]
    public async Task Stored_Current_OfferLost_Row_Serves_The_Page_Unchanged()
    {
        var item = await SingleInboxItemAsync(await CaptureOfferLostPushPayloadAsync());

        item.DeepLink.Should().Be(NotificationDeepLinkResolver.InboxRoot);
        item.Ref.Should().Be(RequestId);
    }

    // The exact dictionary OfferPushNotifier hands to the generic seam, which the
    // notification-service stores verbatim as the inbox row's payload (PLAN-P02 §2a).
    private static async Task<JObject> CaptureOfferLostPushPayloadAsync()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyOfferLostAsync(LoserJeeberId, RequestId, OfferId, CancellationToken.None);

        var sent = push.Sends.Should().ContainSingle().Subject;
        return JObject.FromObject(sent);
    }

    private static async Task<JeebNotificationItemResponse> SingleInboxItemAsync(JObject payload)
    {
        var wire = new JObject
        {
            ["total"] = 1,
            ["items"] = new JArray(new JObject
            {
                ["notification_id"] = "notif-offer-lost-p02",
                ["notification_type"] = JeebGenericEventTypes.OfferLostEventType,
                ["title"] = payload.Value<string>("title"),
                ["description"] = payload.Value<string>("body"),
                ["status"] = "unread",
                ["at"] = "2026-09-06T10:00:00.1234567+00:00",
                ["metadata"] = new JObject
                {
                    ["event_type"] = JeebGenericEventTypes.OfferLostEventType,
                },
                ["payload"] = payload,
            }),
        };

        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wire.ToString(), Encoding.UTF8, "application/json"),
        });

        using var factory = NewFactoryWithNotificationStub(stub);
        using var client = MintBearerClient(factory, LoserJeeberId);

        var response = await client.GetAsync("/v1/notifications");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a producer-shaped row must never fail the caller's whole page, but the answer was {0}", raw);
        var page = Newtonsoft.Json.JsonConvert.DeserializeObject<JeebNotificationsPageResponse>(raw);
        return page!.Items.Should().ContainSingle().Subject;
    }

    private static WebApplicationFactory<Program> NewFactoryWithNotificationStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceNotificationClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://notif.test/") };
                    return new ServiceNotificationClient("http://notif.test/", http);
                });
            });
        });

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory, string sub)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"] ?? "jeeb-gateway",
            audience: config["Jwt:Audience"] ?? "jeeb-clients",
            claims: new[] { new Claim("sub", sub), new Claim("roles", "driver") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }

    private sealed class RecordingUserPushClient : ServicePushNotificationClient
    {
        public RecordingUserPushClient() : base("http://localhost", new HttpClient()) { }

        public List<object> Sends { get; } = new();

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Sends.Add(body.Payload);
            return Task.FromResult(new SentPayloadResponse { Message = "ok", Timestamp = DateTimeOffset.UtcNow });
        }
    }
}
