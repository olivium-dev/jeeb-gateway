using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Push;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// BUILD-NEWREQ-PUSH — the request-created → "finding jeebers" push trigger. Two layers,
/// mirroring <see cref="OfferPushNotifierTests"/>:
///   • unit tests on <see cref="NewRequestPushNotifier"/> itself against a recording
///     <see cref="ServicePushNotificationClient"/> subclass that overrides the new
///     hand-written TOPIC seam — the FLAT new-request payload shape (type=new_request,
///     both requestId + request_id, tierId, NO nested "data"), body trimming at 80,
///     the target topic (<c>jeeb_jeebers</c>), degrade-don't-fail, and blank-id no-op; and
///   • END-TO-END wiring through the REAL JSON create pipeline
///     (<c>POST /v1/requests</c>, application/json → JeebRequestsController) with the push
///     client replaced by a recorder — proving a clean create fires exactly ONE topic
///     publish, that a throwing push client never breaks the 201, and that the 400
///     (blank description) and 409 (BR-9 cap) reject paths publish NOTHING.
/// </summary>
public class NewRequestPushNotifierTests
{
    private const string RequestId = "req-99";
    private const string Tier = "flash";

    // ---------------------------------------------------------------------
    // Unit — the notifier in isolation against a recording topic client.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NewRequest_BroadcastsToJeebersTopic_WithFlatPayload()
    {
        var push = new RecordingTopicPushClient();
        var notifier = new NewRequestPushNotifier(push, NullLogger<NewRequestPushNotifier>.Instance);

        await notifier.NotifyNewRequestAsync(RequestId, Tier, "Pick up a package", CancellationToken.None);

        push.Sends.Should().ContainSingle();
        var send = push.Sends.Single();
        send.Topic.Should().Be("jeeb_jeebers", "the new-request push blasts the jeebers audience topic");
        send.Topic.Should().Be(JeebPushTopicMap.JeebersTopic);

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["title"].Should().Be("New delivery request");
        payload["type"].Should().Be("new_request");
        payload["category"].Should().Be("delivery");
        // Both id variants are carried flat so the mobile deep-link (routes /orders/:id from
        // delivery_id/order_id/requestId fallback) resolves regardless of which key it reads.
        payload["requestId"].Should().Be(RequestId);
        payload["request_id"].Should().Be(RequestId);
        payload["tierId"].Should().Be(Tier);
        // Routing fields are flat top-level entries — no nested "data" object.
        payload.Should().NotContainKey("data");
        ((string)payload["body"]!).Should().Contain("Pick up a package");
        ((string)payload["body"]!).Should().Contain(Tier);
    }

    [Fact]
    public async Task Body_IsTrimmedTo80Chars_ThenTierSuffixAppended()
    {
        var push = new RecordingTopicPushClient();
        var notifier = new NewRequestPushNotifier(push, NullLogger<NewRequestPushNotifier>.Instance);

        var longDescription = new string('x', 100);
        await notifier.NotifyNewRequestAsync(RequestId, Tier, longDescription, CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.Sends.Single().Payload;
        var body = (string)payload["body"]!;

        // The 80-char preview cap applies to the description ONLY; the " • {tier}" suffix
        // is added after the trim, so it survives even when the description is over-length.
        body.Should().Be(new string('x', 80) + " • " + Tier);
    }

    [Fact]
    public async Task NoTier_OmitsTierSuffix_AndCarriesNullTierId()
    {
        var push = new RecordingTopicPushClient();
        var notifier = new NewRequestPushNotifier(push, NullLogger<NewRequestPushNotifier>.Instance);

        await notifier.NotifyNewRequestAsync(RequestId, tierId: null, "Short desc", CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.Sends.Single().Payload;
        ((string)payload["body"]!).Should().Be("Short desc", "no tier → no ' • {tier}' suffix");
        payload["tierId"].Should().BeNull();
    }

    [Fact]
    public async Task PushServiceFault_IsSwallowed_NeverThrows()
    {
        var push = new RecordingTopicPushClient { Throw = true };
        var notifier = new NewRequestPushNotifier(push, NullLogger<NewRequestPushNotifier>.Instance);

        // Degrade-don't-fail: a push blip must never surface to the create path.
        var act = async () => await notifier.NotifyNewRequestAsync(RequestId, Tier, "desc", CancellationToken.None);
        await act.Should().NotThrowAsync();
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task BlankRequestId_PushesNothing()
    {
        var push = new RecordingTopicPushClient();
        var notifier = new NewRequestPushNotifier(push, NullLogger<NewRequestPushNotifier>.Instance);

        await notifier.NotifyNewRequestAsync(requestId: "  ", Tier, "desc", CancellationToken.None);

        push.Sends.Should().BeEmpty();
        push.Attempts.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // E2E wiring — the REAL POST /v1/requests JSON pipeline calls the notifier.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task JsonCreate_TriggersExactlyOneTopicPublish_WithNewRequestType_AndRequestId()
    {
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Pick up groceries"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<CreatedRequestDto>())!;

        push.Sends.Should().ContainSingle("exactly one topic publish per accepted create");
        var send = push.Sends.Single();
        send.Topic.Should().Be(JeebPushTopicMap.JeebersTopic);

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["type"].Should().Be("new_request");
        payload["requestId"].Should().Be(dto.Id);
        payload["request_id"].Should().Be(dto.Id);
        payload["tierId"].Should().Be(Tier);
        payload.Should().NotContainKey("data");
    }

    [Fact]
    public async Task JsonCreate_WhenPushClientThrows_StillReturns201()
    {
        var push = new RecordingTopicPushClient { Throw = true };
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Deliver documents"));

        // Degrade-don't-fail end-to-end: a throwing push service does not flip the 201.
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task JsonCreate_BlankDescription_Returns400_AndPublishesNothing()
    {
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("   "));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Sends.Should().BeEmpty("a rejected (400) create never reaches the push hook");
        push.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task JsonCreate_OverCap_Returns409_AndPublishesNothingForTheBlockedRequest()
    {
        var push = new RecordingTopicPushClient();
        using var factory = NewFactory(push);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        // BR-9: the first three succeed (three topic publishes)...
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/v1/requests", ValidPayload($"req {i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"creation {i} should succeed under the cap");
        }

        push.Sends.Should().HaveCount(3, "one publish per accepted create under the cap");

        // ...the fourth is blocked with 409 BEFORE the row is created — no extra publish.
        var blocked = await client.PostAsJsonAsync("/v1/requests", ValidPayload("fourth"));
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        push.Sends.Should().HaveCount(3, "a blocked (409) create never reaches the push hook");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(RecordingTopicPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Replace the deployed :10040 push client with the recorder so no real
                    // network call happens and the emitted topic/payload are asserted.
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);
                });
            });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    /// <summary>Minimum valid JSON create body — description + tier + WGS84 pickup/dropoff.</summary>
    private static object ValidPayload(string description) => new
    {
        description,
        tierId = Tier,
        pickupLocation = new { lat = 33.88, lng = 35.50 },
        dropoffLocation = new { lat = 33.89, lng = 35.51 },
    };

    private sealed record CreatedRequestDto(string Id, string ClientId, string Status, string Description);

    private sealed record SendRecord(string Topic, object Payload);

    /// <summary>Recording stand-in for the deployed push client; overrides the hand-written
    /// send-to-TOPIC seam the new-request notifier uses. The base ctor needs a base URL +
    /// HttpClient.</summary>
    private sealed class RecordingTopicPushClient : ServicePushNotificationClient
    {
        public RecordingTopicPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SendRecord> Sends { get; } = new();
        public int Attempts;
        public bool Throw { get; init; }

        public override Task<SentPayloadResponse> Send_notification_to_topicAsync(
            string topicName, SentPayloadToTopicRequest body, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            if (Throw)
            {
                throw new InvalidOperationException("push service unavailable");
            }
            Sends.Enqueue(new SendRecord(topicName, body.Payload));
            return Task.FromResult(new SentPayloadResponse { Message = "ok", Timestamp = DateTimeOffset.UtcNow });
        }
    }
}
