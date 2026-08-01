using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw5Pack;

/// <summary>
/// GW5 / W1.6-gateway — G1: the WIRE. What the gateway actually sends to chat-service's
/// additive settle route, and what it does with the answer.
///
/// <para>Everything here is asserted against
/// <c>chat-service/documentation/CONVERSATION-SETTLE-CONTRACT.md</c>, which CB4 froze.
/// A fake <see cref="IJeebConversationClient"/> cannot check any of it — it is the layer
/// BELOW the fake — so these drive the REAL <see cref="JeebConversationClient"/> over a
/// stub <see cref="HttpMessageHandler"/> and read the bytes.</para>
///
/// <para>The response half matters more than it looks. chat-service NESTS the settled
/// conversation under <c>conversation</c>; Newtonsoft binds a non-matching shape to an
/// all-default object and throws NOTHING. Without an explicit guard, a contract drift
/// would arrive here as a perfectly healthy 200 carrying an EMPTY conversation id — the
/// same silent-drop class that once emptied whole chat threads on a 200. G1.3/G1.4 are
/// the negative and positive halves of that guard.</para>
/// </summary>
public class G1_SettleWireContractTests
{
    private const string ConversationId = "conv-1";
    private const string Winner = "jeeber-win";

    private const string NestedEnvelope = """
        {
          "conversation": {
            "conversation_id": "conv-1",
            "correlation_key": "req-1",
            "phase": "accepted",
            "participants": [
              { "user_id": "client-owner", "role_in_convo": "client", "removed_at": null },
              { "user_id": "jeeber-win", "role_in_convo": "jeeber_winner", "removed_at": null }
            ]
          },
          "seated": true,
          "role_changed": false,
          "phase_changed": true,
          "removed_user_ids": ["jeeber-lost"],
          "already_settled": false
        }
        """;

    /// <summary>
    /// G1.1 — the route. ONE POST to <c>api/conversations/{id}/settle</c>. Not two calls,
    /// not a PATCH to /phase.
    /// </summary>
    [Fact]
    public async Task Settle_PostsToTheSettleRoute_Once()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, NestedEnvelope);
        var client = NewClient(handler);

        await client.SettleAsync(ConversationId, NewRequest(), CancellationToken.None);

        handler.Calls.Should().Be(1);
        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastUri!.AbsolutePath.Should().Be("/api/conversations/conv-1/settle");
    }

    /// <summary>
    /// G1.2 — the request body, field by field, in chat-service's snake_case wire names.
    /// A rename on either side is a breaking change to a SHARED service, so the names are
    /// pinned here rather than left to inference.
    /// </summary>
    [Fact]
    public async Task Settle_SendsTheFrozenSnakeCaseBody()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, NestedEnvelope);
        var client = NewClient(handler);

        await client.SettleAsync(ConversationId, NewRequest(), CancellationToken.None);

        var body = JObject.Parse(handler.LastBody!);
        body["phase"]!.Value<string>().Should().Be("accepted");
        body["winner_user_id"]!.Value<string>().Should().Be(Winner);
        body["winner_role_in_convo"]!.Value<string>().Should().Be("jeeber_winner");
        body["remove_others"]!.Value<bool>().Should().BeTrue();

        // The contract accepts NO idempotency key — the request states an end state, so
        // there is no increment to de-duplicate. Sending one would be inventing a field.
        body.Property("idempotency_key").Should().BeNull();
        handler.LastHeaders.Should().NotContainKey("Idempotency-Key");
    }

    /// <summary>
    /// G1.3 — POSITIVE control for the envelope guard: the real nested shape binds, and
    /// every outcome flag is read off the wire rather than defaulted.
    /// </summary>
    [Fact]
    public async Task Settle_BindsTheNestedEnvelope()
    {
        var client = NewClient(new CapturingHandler(HttpStatusCode.OK, NestedEnvelope));

        var settled = await client.SettleAsync(ConversationId, NewRequest(), CancellationToken.None);

        settled.Conversation.Should().NotBeNull();
        settled.Conversation!.ConversationId.Should().Be("conv-1");
        settled.Conversation.Phase.Should().Be("accepted");
        settled.Conversation.Participants.Should().HaveCount(2);
        settled.Seated.Should().BeTrue();
        settled.PhaseChanged.Should().BeTrue();
        settled.RoleChanged.Should().BeFalse();
        settled.AlreadySettled.Should().BeFalse();
        settled.RemovedUserIds.Should().ContainSingle().Which.Should().Be("jeeber-lost");
    }

    /// <summary>
    /// G1.4 — NEGATIVE control, and the one that earns the guard its place: a 200 whose
    /// body is a FLAT <c>ConversationResponse</c> (the shape a planning document
    /// described, and the shape a caller would get by binding to the wrong type) must
    /// RAISE. Without the guard this call returns a perfectly ordinary object with an
    /// empty conversation id and no error anywhere.
    /// </summary>
    [Fact]
    public async Task Settle_WhenBodyIsFlatConversation_Raises_InsteadOfReturningAHusk()
    {
        const string flat = """
            { "conversation_id": "conv-1", "correlation_key": "req-1", "phase": "accepted", "participants": [] }
            """;
        var client = NewClient(new CapturingHandler(HttpStatusCode.OK, flat));

        var act = async () => await client.SettleAsync(ConversationId, NewRequest(), CancellationToken.None);

        var raised = (await act.Should().ThrowAsync<JeebConversationApiException>()).Which;
        raised.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        // The reason travels in Body, not Message — JeebConversationApiException's Message
        // is a fixed "failed with {status}" string for every upstream fault.
        raised.Body.Should().Contain("conversation.conversation_id");
    }

    /// <summary>
    /// G1.5 — an upstream refusal is surfaced verbatim, not collapsed. The reconciler
    /// distinguishes a 404 (no such conversation) from every other fault, so the status
    /// must survive the hop.
    /// </summary>
    [Fact]
    public async Task Settle_ForwardsTheUpstreamStatusVerbatim()
    {
        var client = NewClient(new CapturingHandler(
            HttpStatusCode.NotFound, """{ "message": "conversation not found" }"""));

        var act = async () => await client.SettleAsync(ConversationId, NewRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<JeebConversationApiException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------------

    private static SettleJeebConversationRequest NewRequest() => new()
    {
        Phase = "accepted",
        WinnerUserId = Winner,
        WinnerRoleInConvo = "jeeber_winner",
        RemoveOthers = true,
    };

    private static JeebConversationClient NewClient(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://chat.test/") });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _jsonBody;

        public int Calls { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public CapturingHandler(HttpStatusCode status, string jsonBody)
        {
            _status = status;
            _jsonBody = jsonBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastHeaders.Clear();
            foreach (var h in request.Headers)
            {
                LastHeaders[h.Key] = string.Join(",", h.Value);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
