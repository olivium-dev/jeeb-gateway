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
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// BUILD-OFFER-PUSH — the offer-submitted → push-notification trigger. Two layers:
///   • unit tests on <see cref="OfferPushNotifier"/> itself against a recording
///     <see cref="ServicePushNotificationClient"/> subclass — recipient resolution
///     (the request's customer/clientId), the FLAT offer payload shape (type=offer,
///     category=delivery, requestId+request_id, offerId), and degrade-don't-fail; and
///   • an END-TO-END wiring test through the REAL submit pipeline
///     (<c>POST /requests/{id}/offers</c>) with the push client replaced by a recorder,
///     proving the controller fires exactly one push to the request's clientId with
///     type=offer + requestId, and that a throwing push client never breaks the 201.
/// </summary>
public class OfferPushNotifierTests
{
    private const string Client = "client-sami";
    private const string RequestId = "req-42";
    private const string OfferId = "offer-7";

    [Fact]
    public async Task NewOffer_NotifiesCustomer_WithFlatOfferPayload()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyNewOfferAsync(Client, RequestId, OfferId, fee: 12.5m, CancellationToken.None);

        push.Sends.Should().ContainSingle();
        var send = push.Sends.Single();
        send.UserId.Should().Be(Client, "the push goes to the request's customer (clientId)");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["title"].Should().Be("New offer on your request");
        payload["type"].Should().Be("offer");
        payload["category"].Should().Be("delivery");
        // Both id variants are carried flat so the mobile deep-link (routes /orders/:id from
        // delivery_id/order_id/requestId fallback) resolves regardless of which key it reads.
        payload["requestId"].Should().Be(RequestId);
        payload["request_id"].Should().Be(RequestId);
        payload["offerId"].Should().Be(OfferId);
        // Routing fields are flat top-level entries — no nested "data" object.
        payload.Should().NotContainKey("data");
        ((string)payload["body"]!).Should().Contain("12.5");
    }

    [Fact]
    public async Task PushServiceFault_IsSwallowed_NeverThrows()
    {
        var push = new RecordingUserPushClient { Throw = true };
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        // Degrade-don't-fail: a push blip must never surface to the offer-submit path.
        var act = async () => await notifier.NotifyNewOfferAsync(Client, RequestId, OfferId, 5m, CancellationToken.None);
        await act.Should().NotThrowAsync();
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task MissingClientId_PushesNothing()
    {
        var push = new RecordingUserPushClient();
        var notifier = new OfferPushNotifier(push, NullLogger<OfferPushNotifier>.Instance);

        await notifier.NotifyNewOfferAsync(clientId: " ", RequestId, OfferId, 5m, CancellationToken.None);

        push.Sends.Should().BeEmpty();
        push.Attempts.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // E2E wiring — the REAL POST /requests/{id}/offers pipeline calls the notifier.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Submit_TriggersExactlyOnePush_ToCustomer_WithOfferType_AndRequestId()
    {
        var push = new RecordingUserPushClient();
        using var factory = NewFactory(push);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, $"jeeber-{Guid.NewGuid()}");

        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20, note = "On my way" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<OfferDto>())!;

        push.Sends.Should().ContainSingle("exactly one offer push per accepted submission");
        var send = push.Sends.Single();
        send.UserId.Should().Be(clientId, "the customer (request owner) is notified, not the offering jeeber");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["type"].Should().Be("offer");
        payload["category"].Should().Be("delivery");
        payload["requestId"].Should().Be(requestId);
        payload["request_id"].Should().Be(requestId);
        payload["offerId"].Should().Be(dto.Id);
    }

    [Fact]
    public async Task Submit_WhenPushClientThrows_StillReturns201()
    {
        var push = new RecordingUserPushClient { Throw = true };
        using var factory = NewFactory(push);

        var (_, requestId) = await SeedRequestAsync(factory);
        var jeeber = JeeberClient(factory, $"jeeber-{Guid.NewGuid()}");

        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 6m, etaMinutes = 15 });

        // Degrade-don't-fail end-to-end: a throwing push service does not flip the 201.
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(RecordingUserPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Replace the deployed :10040 push client with the recorder so no real
                    // network call happens and the emitted payload/recipient are asserted.
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);
                });
            });

    private static async Task<(string clientId, string requestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package",
        }, default);
        return (clientId, created.Id);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }

    private sealed record SendRecord(string UserId, object Payload);

    /// <summary>Recording stand-in for the deployed push client; overrides the single
    /// send-to-user seam both notifiers use. The base ctor needs a base URL + HttpClient.</summary>
    private sealed class RecordingUserPushClient : ServicePushNotificationClient
    {
        public RecordingUserPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SendRecord> Sends { get; } = new();
        public int Attempts { get; private set; }
        public bool Throw { get; init; }

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Throw)
            {
                throw new InvalidOperationException("push service unavailable");
            }
            Sends.Enqueue(new SendRecord(user_id, body.Payload));
            return Task.FromResult(new SentPayloadResponse { Message = "ok", Timestamp = DateTimeOffset.UtcNow });
        }
    }
}
