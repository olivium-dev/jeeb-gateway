using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Chat;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.Chat;

/// <summary>
/// REAL-WIRE CONTRACT TEST for the gateway↔chat-service seam.
///
/// Lesson from a prior seam bug: an in-memory double can silently diverge from the
/// real JSON shape the upstream emits, so a green suite hides a wire-binding break.
/// These tests therefore drive the PRODUCTION <see cref="ChatServiceClient"/> over a
/// stub <see cref="HttpMessageHandler"/> that returns the LITERAL chat-service
/// response body — byte-for-byte the casing the chat-service serializer produces —
/// and assert the DTO binds.
///
/// Casing comes straight from the chat-service source:
///   - <c>ChatService.Persistence.PagedList&lt;T&gt;</c> has NO Newtonsoft
///     <c>[JsonProperty]</c> attributes, and the service uses Newtonsoft default
///     settings (no camel-case resolver), so the envelope is PascalCase:
///     <c>NextPageToken, PageCount, TotalCount, Items</c>.
///   - <c>ChatService.Domain.Response.MessageResponse</c> annotates every field
///     with <c>[JsonProperty("camelName")]</c>, so message items are camelCase:
///     <c>guid, createdAt, text, payload, memberId, channelId, sessionId,
///     messageId, modifiedAt, ...</c>.
///
/// The literal bodies below are mixed-case ON PURPOSE — that is the actual wire.
/// If the chat-service contract changes casing, this test breaks instead of prod.
/// </summary>
public sealed class ChatServiceClientRealWireTests
{
    // Verbatim envelope the chat-service emits for
    // GET /api/channels/{channelId}/messages — PascalCase PagedList wrapper,
    // camelCase MessageResponse items (newest-first), with a nextPageToken cursor.
    private const string ListMessagesBody = """
    {
      "NextPageToken": "msg-aaaa-older-cursor",
      "PageCount": 2,
      "TotalCount": 37,
      "Items": [
        {
          "guid": "guid-newest",
          "createdAt": "2026-05-30T10:15:30Z",
          "text": "newest message",
          "payload": "",
          "memberId": "member-alice",
          "channelId": "chan-xyz",
          "sessionId": "sess-1",
          "parentId": null,
          "messageId": "msg-newest",
          "isActive": true,
          "isDeleted": false,
          "isEdited": false,
          "modifiedAt": "2026-05-30T10:16:00Z"
        },
        {
          "guid": "guid-older",
          "createdAt": "2026-05-30T10:10:00Z",
          "text": "older message",
          "payload": "",
          "memberId": "member-bob",
          "channelId": "chan-xyz",
          "sessionId": "sess-2",
          "parentId": null,
          "messageId": "msg-older",
          "isActive": true,
          "isDeleted": false,
          "isEdited": false,
          "modifiedAt": null
        }
      ]
    }
    """;

    [Fact]
    public async Task GetChannelMessagesAsync_Binds_Real_ChatService_PagedList_Json()
    {
        // Arrange: real ChatServiceClient over a stub handler returning the literal body.
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ListMessagesBody, Encoding.UTF8, "application/json")
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://chat.test/") };
        var sut = new ChatServiceClient(http, new InMemoryChatTopologyMap());

        // Act
        var page = await sut.GetChannelMessagesAsync("chan-xyz", limit: 25, beforeMessageId: null, ct: default);

        // Assert — request shape: hits the generic list endpoint with the limit query.
        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/api/channels/chan-xyz/messages");
        captured.RequestUri.Query.Should().Contain("limit=25");

        // Assert — envelope (PascalCase PagedList) bound through STJ Web defaults.
        page.NextPageToken.Should().Be("msg-aaaa-older-cursor");
        page.TotalCount.Should().Be(37);
        page.Items.Should().HaveCount(2);

        // Assert — camelCase MessageResponse items projected onto ChatMessageDto.
        // messageId wins over guid for the id; createdAt -> SentAt; modifiedAt -> ReadAt.
        var newest = page.Items[0];
        newest.Id.Should().Be("msg-newest");
        newest.ConversationId.Should().Be("chan-xyz");
        newest.Text.Should().Be("newest message");
        newest.SentAt.Should().Be(DateTimeOffset.Parse("2026-05-30T10:15:30Z"));
        newest.ReadAt.Should().Be(DateTimeOffset.Parse("2026-05-30T10:16:00Z"));

        var older = page.Items[1];
        older.Id.Should().Be("msg-older");
        older.Text.Should().Be("older message");
        older.ReadAt.Should().BeNull(); // modifiedAt was null on the wire
    }

    [Fact]
    public async Task GetChannelMessagesAsync_Appends_Before_Cursor_When_Provided()
    {
        // Proves the cursor primitive: a non-null beforeMessageId is sent as ?before=.
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ListMessagesBody, Encoding.UTF8, "application/json")
            };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://chat.test/") };
        var sut = new ChatServiceClient(http, new InMemoryChatTopologyMap());

        await sut.GetChannelMessagesAsync("chan-xyz", limit: 10, beforeMessageId: "msg-cursor", ct: default);

        captured!.RequestUri!.Query.Should().Contain("limit=10");
        captured.RequestUri.Query.Should().Contain("before=msg-cursor");
    }

    [Fact]
    public async Task GetConversationAsync_Pages_Through_List_Endpoint_Oldest_First()
    {
        // Proves the conversation rewire: GetConversationAsync no longer reads a
        // single last-message channel summary — it pages the generic list endpoint
        // via GetChannelMessagesAsync until `limit` is reached, following the
        // nextPageToken cursor, then returns OLDEST-first for a chronological
        // transcript. We drive the REAL client and serve two literal pages.

        // First establish the topology for the (alice, bob) pair by routing a send
        // through the real client (members/channel/sessions/post/read-back), so
        // GetConversationAsync can resolve the deterministic channel. Every POST
        // returns an IdentityResponse{id}; the single-message GET returns one
        // camelCase MessageResponse.
        var page1 = """
        {
          "NextPageToken": "cursor-to-page2",
          "PageCount": 2,
          "TotalCount": 3,
          "Items": [
            { "guid": "g3", "messageId": "m3", "createdAt": "2026-05-30T10:30:00Z", "text": "third (newest)", "memberId": "mem-alice", "channelId": "chan-1" },
            { "guid": "g2", "messageId": "m2", "createdAt": "2026-05-30T10:20:00Z", "text": "second", "memberId": "mem-bob", "channelId": "chan-1" }
          ]
        }
        """;
        var page2 = """
        {
          "NextPageToken": null,
          "PageCount": 2,
          "TotalCount": 3,
          "Items": [
            { "guid": "g1", "messageId": "m1", "createdAt": "2026-05-30T10:10:00Z", "text": "first (oldest)", "memberId": "mem-alice", "channelId": "chan-1" }
          ]
        }
        """;

        var listCalls = 0;
        var handler = new StubHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;

            // List-messages endpoint: serve page1 then page2 by cursor.
            if (path.EndsWith("/messages") && req.Method == HttpMethod.Get)
            {
                listCalls++;
                var body = query.Contains("before=cursor-to-page2") ? page2 : page1;
                return Json(body);
            }

            // Topology bootstrap: every POST (members, channels, join, message)
            // returns an IdentityResponse; deterministic ids keep the channel stable.
            if (req.Method == HttpMethod.Post)
                return Json("""{ "id": "chan-1" }""");

            // Single-message read-back after send.
            return Json("""{ "guid": "g0", "messageId": "m0", "createdAt": "2026-05-30T10:00:00Z", "text": "seed", "memberId": "mem-alice", "channelId": "chan-1" }""");
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://chat.test/") };
        var topology = new InMemoryChatTopologyMap();
        var sut = new ChatServiceClient(http, topology);

        // Establish the deterministic channel for the pair.
        await sut.SendMessageAsync("alice", "bob", "seed", default);

        // Act — request all 3 messages; client must follow the cursor across 2 pages.
        var convo = await sut.GetConversationAsync("alice", "bob", limit: 10, default);

        // Assert — paged across both pages (2 list calls) and returned oldest-first.
        listCalls.Should().Be(2);
        convo.Select(m => m.Id).Should().ContainInOrder("m1", "m2", "m3");
        convo.Select(m => m.Text).Should()
            .ContainInOrder("first (oldest)", "second", "third (newest)");
    }

    [Fact]
    public async Task GetChannelMessagesAsync_Returns_Empty_On_404()
    {
        // A 404 from the upstream (unknown channel) is an empty page, never a throw.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://chat.test/") };
        var sut = new ChatServiceClient(http, new InMemoryChatTopologyMap());

        var page = await sut.GetChannelMessagesAsync("missing", limit: 25, beforeMessageId: null, ct: default);

        page.Items.Should().BeEmpty();
        page.NextPageToken.Should().BeNull();
        page.TotalCount.Should().Be(0);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }
}
