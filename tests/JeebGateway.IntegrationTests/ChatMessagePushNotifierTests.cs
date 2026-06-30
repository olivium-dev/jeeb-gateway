using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// BUILD-CHAT-PUSH — the chat-message → push-notification trigger. These tests
/// exercise <see cref="ChatMessagePushNotifier"/> (the new component the two chat
/// send handlers call) against a REAL <see cref="InMemoryRequestsStore"/> and a
/// recording <see cref="ServicePushNotificationClient"/> subclass, proving:
///   • recipient resolution from the conversation id stamped on the request row,
///   • the author is excluded (no self-push),
///   • the FLAT payload shape (title/body + top-level conversationId/requestId/type; no nested data object),
///   • best-effort / degrade-don't-fail (a push-service fault never throws),
///   • unknown conversation → no push.
/// </summary>
public class ChatMessagePushNotifierTests
{
    private const string Client = "client-nour";
    private const string Jeeber = "jeeber-karim";
    private const string ConversationId = "conv-abc";

    [Fact]
    public async Task JeeberSends_NotifiesClientOnly_WithChatPayload()
    {
        var (store, requestId) = await SeedAcceptedAsync();
        var push = new RecordingPushClient();
        var notifier = new ChatMessagePushNotifier(store, push, NullLogger<ChatMessagePushNotifier>.Instance);

        await notifier.NotifyNewMessageAsync(ConversationId, authorUserId: Jeeber, "On my way", CancellationToken.None);

        push.Sends.Should().ContainSingle();
        var send = push.Sends.Single();
        send.UserId.Should().Be(Client, "the author (jeeber) is excluded; the client is the other party");

        var payload = (IDictionary<string, object?>)send.Payload;
        payload["title"].Should().Be("New message");
        payload["body"].Should().Be("On my way");
        // Routing fields are FLAT top-level string entries -- no nested "data" sub-object --
        // so the push service maps each to its own FCM data key and the client needs no hoist.
        payload.Should().NotContainKey("data", "routing fields must be flat, not nested under a 'data' object");
        payload["conversationId"].Should().Be(ConversationId);
        payload["requestId"].Should().Be(requestId);
        payload["type"].Should().Be("chat");
    }

    [Fact]
    public async Task ClientSends_NotifiesAssignedJeeberOnly()
    {
        var (store, _) = await SeedAcceptedAsync();
        var push = new RecordingPushClient();
        var notifier = new ChatMessagePushNotifier(store, push, NullLogger<ChatMessagePushNotifier>.Instance);

        await notifier.NotifyNewMessageAsync(ConversationId, authorUserId: Client, "Where are you?", CancellationToken.None);

        push.Sends.Should().ContainSingle();
        push.Sends.Single().UserId.Should().Be(Jeeber);
    }

    [Fact]
    public async Task UnknownConversation_PushesNothing()
    {
        var (store, _) = await SeedAcceptedAsync();
        var push = new RecordingPushClient();
        var notifier = new ChatMessagePushNotifier(store, push, NullLogger<ChatMessagePushNotifier>.Instance);

        await notifier.NotifyNewMessageAsync("conv-does-not-exist", authorUserId: Jeeber, "hi", CancellationToken.None);

        push.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task PreAccept_ClientSends_NoJeeberYet_PushesNothing()
    {
        // Broadcasting phase: no winning jeeber on the row yet. The client is the only
        // principal and is the author, so there is no other party to notify.
        var store = new InMemoryRequestsStore(TimeProvider.System);
        var created = await store.CreateAsync(NewInput(Client), CancellationToken.None);
        (await store.GetAsync(created.Id, CancellationToken.None))!.ConversationId = ConversationId;
        var push = new RecordingPushClient();
        var notifier = new ChatMessagePushNotifier(store, push, NullLogger<ChatMessagePushNotifier>.Instance);

        await notifier.NotifyNewMessageAsync(ConversationId, authorUserId: Client, "hello?", CancellationToken.None);

        push.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task PushServiceFault_IsSwallowed_NeverThrows()
    {
        var (store, _) = await SeedAcceptedAsync();
        var push = new RecordingPushClient { Throw = true };
        var notifier = new ChatMessagePushNotifier(store, push, NullLogger<ChatMessagePushNotifier>.Instance);

        // Degrade-don't-fail: the chat message was already accepted; a push blip must not throw.
        var act = async () => await notifier.NotifyNewMessageAsync(ConversationId, Jeeber, "x", CancellationToken.None);
        await act.Should().NotThrowAsync();
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static async Task<(InMemoryRequestsStore store, string requestId)> SeedAcceptedAsync()
    {
        var store = new InMemoryRequestsStore(TimeProvider.System);
        var created = await store.CreateAsync(NewInput(Client), CancellationToken.None);
        var accepted = await store.TryAcceptByJeeberAsync(created.Id, Jeeber, limit: 2, DateTimeOffset.UtcNow, CancellationToken.None);
        accepted.Should().NotBeNull();
        // The conversation id is stamped onto the row at create/accept in production
        // (DurableRequestsStore create-time + patch 0007 at accept); set it here directly.
        (await store.GetAsync(created.Id, CancellationToken.None))!.ConversationId = ConversationId;
        return (store, created.Id);
    }

    private static CreateRequestInput NewInput(string clientId) => new()
    {
        ClientId = clientId,
        Description = "Deliver the parcel",
        TierId = "flash",
        PickupLocation = new GeoPoint { Lat = 33.5138, Lng = 36.2765 },
        DropoffLocation = new GeoPoint { Lat = 33.52, Lng = 36.28 },
    };

    private sealed record SendRecord(string UserId, object Payload);

    /// <summary>Recording stand-in for the deployed push client; overrides the single
    /// send seam the notifier uses. The base ctor needs a base URL + HttpClient.</summary>
    private sealed class RecordingPushClient : ServicePushNotificationClient
    {
        public RecordingPushClient() : base("http://localhost", new HttpClient()) { }

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
