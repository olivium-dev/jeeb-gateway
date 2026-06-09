using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S07 spine: gateway post-accept BFF orchestration. When the offer-service accept
/// saga returns <see cref="OfferAcceptStatus.Accepted"/>, the gateway (the SOLE
/// cross-service composer — offer/delivery/chat services never call each other)
/// must, BEFORE emitting 200:
/// <list type="bullet">
///   <item><b>H6b</b> sync its own request ledger row to <c>accepted</c> + the
///     winning <c>jeeberId</c> so <c>GET /requests/{id}</c> reflects it;</item>
///   <item><b>H6d</b> advance the broadcasting conversation (add winning jeeber /
///     drop losers) via <see cref="IConversationProvisioner.AdvanceToAcceptedAsync"/>.</item>
/// </list>
/// EVERY side-effect is degrade-don't-fail: a downstream blip must never turn a
/// successful upstream accept into a 5xx. These tests fake the offer-service client
/// and the conversation provisioner so the gateway's orchestration is asserted
/// deterministically without a live upstream.
/// </summary>
public class OfferAcceptOrchestrationTests
{
    // -----------------------------------------------------------------
    // H6b — request-sync
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_Syncs_Request_Ledger_To_Accepted_With_Winning_Jeeber()
    {
        var fakeOffer = AcceptedFake("offer-h6b", winningJeeberId: "jeeber-win");
        var fakeChat = new RecordingConversationProvisioner();
        using var factory = NewFactory(fakeOffer, fakeChat);

        // Seed a real request row (mirrors POST /requests) so the ledger sync has a
        // row to mutate; capture its id as the offer's requestId.
        var requestId = await SeedRequestAsync(factory, clientId: "client-sami");
        SeedRouting(factory, offerId: "offer-h6b", requestId: requestId, jeeberId: "jeeber-win");

        var resp = await ClientActor(factory, "client-sami")
            .PostAsync("/offers/offer-h6b/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // H6b: GET /requests/{id} now reflects accepted + the winning jeeber.
        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, default);
        row.Should().NotBeNull();
        row!.Status.Should().Be(RequestStatus.Accepted);
        row.JeeberId.Should().Be("jeeber-win");
        row.AcceptedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Accept_Without_Envelope_Jeeber_Does_Not_Write_Blank_Jeeber()
    {
        // The upstream envelope omitted JeeberId — the gateway must NOT overwrite the
        // request's jeeberId with an empty string. The accept still returns 200 (the
        // saga committed upstream); the ledger simply stays unsynced.
        var fakeOffer = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire { AcceptedOfferId = "offer-nojeeber", JeeberId = null }
            }
        };
        var fakeChat = new RecordingConversationProvisioner();
        using var factory = NewFactory(fakeOffer, fakeChat);

        var requestId = await SeedRequestAsync(factory, clientId: "client-nj");
        SeedRouting(factory, offerId: "offer-nojeeber", requestId: requestId, jeeberId: "bidder-x");

        var resp = await ClientActor(factory, "client-nj")
            .PostAsync("/offers/offer-nojeeber/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, default);
        // No blank jeeber written; status not flipped from the omitted-jeeber sync.
        row!.JeeberId.Should().BeNull();
        // The conversation advance is also skipped when there is no winning jeeber.
        fakeChat.AdvanceCallCount.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // H6d — conversation advance
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_Advances_Conversation_With_Winning_Jeeber()
    {
        var fakeOffer = AcceptedFake("offer-h6d", winningJeeberId: "jeeber-kamal");
        var fakeChat = new RecordingConversationProvisioner();
        using var factory = NewFactory(fakeOffer, fakeChat);

        // Seed a request that already carries a broadcasting conversation id (as it
        // would after create-time provisioning), so the advance has a channel to act on.
        var requestId = await SeedRequestAsync(factory, clientId: "client-sami", conversationId: "conv-123");
        SeedRouting(factory, offerId: "offer-h6d", requestId: requestId, jeeberId: "jeeber-kamal");

        var resp = await ClientActor(factory, "client-sami")
            .PostAsync("/offers/offer-h6d/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // H6d: the provisioner (the SOLE chat caller) was advanced with the request's
        // conversation id and the winning jeeber.
        fakeChat.AdvanceCallCount.Should().Be(1);
        fakeChat.LastConversationId.Should().Be("conv-123");
        fakeChat.LastWinningJeeberId.Should().Be("jeeber-kamal");
    }

    [Fact]
    public async Task Accept_With_No_Conversation_Still_Advances_With_Null_ConversationId()
    {
        // When the request never got a broadcasting conversation (chat was down at
        // create, or auto-create was off), the advance is still invoked with a null
        // conversation id — the provisioner treats that as a no-op. The accept is 200.
        var fakeOffer = AcceptedFake("offer-noconv", winningJeeberId: "jeeber-win");
        var fakeChat = new RecordingConversationProvisioner();
        using var factory = NewFactory(fakeOffer, fakeChat);

        var requestId = await SeedRequestAsync(factory, clientId: "client-nc"); // no conversation id
        SeedRouting(factory, offerId: "offer-noconv", requestId: requestId, jeeberId: "jeeber-win");

        var resp = await ClientActor(factory, "client-nc")
            .PostAsync("/offers/offer-noconv/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeChat.AdvanceCallCount.Should().Be(1);
        fakeChat.LastConversationId.Should().BeNull();
    }

    // -----------------------------------------------------------------
    // Degrade-don't-fail — a chat blip must NOT fail the accept
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_Stays_200_When_Conversation_Advance_Throws()
    {
        var fakeOffer = AcceptedFake("offer-chatblip", winningJeeberId: "jeeber-win");
        var fakeChat = new ThrowingConversationProvisioner();
        using var factory = NewFactory(fakeOffer, fakeChat);

        var requestId = await SeedRequestAsync(factory, clientId: "client-blip", conversationId: "conv-blip");
        SeedRouting(factory, offerId: "offer-chatblip", requestId: requestId, jeeberId: "jeeber-win");

        var resp = await ClientActor(factory, "client-blip")
            .PostAsync("/offers/offer-chatblip/accept", content: null);

        // The chat provisioner threw, but the accept still returns 200 AND the H6b
        // ledger sync (which runs before the chat advance) still committed.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, default);
        row!.Status.Should().Be(RequestStatus.Accepted);
        row.JeeberId.Should().Be("jeeber-win");
    }

    [Fact]
    public async Task Accept_Stays_200_When_Ledger_Row_Unknown_To_Gateway()
    {
        // The routing index resolves a requestId that has no ledger row on this
        // instance (e.g. created on another replica). The H6b sync returns null; the
        // accept must still return 200 (offer-service is authoritative) and the chat
        // advance still runs.
        var fakeOffer = AcceptedFake("offer-noledger", winningJeeberId: "jeeber-win");
        var fakeChat = new RecordingConversationProvisioner();
        using var factory = NewFactory(fakeOffer, fakeChat);

        SeedRouting(factory, offerId: "offer-noledger", requestId: "req-phantom-ledger", jeeberId: "jeeber-win");

        var resp = await ClientActor(factory, "client-nl")
            .PostAsync("/offers/offer-noledger/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeChat.AdvanceCallCount.Should().Be(1);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static FakeOfferServiceClient AcceptedFake(string offerId, string winningJeeberId)
        => new()
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = offerId,
                    JeeberId = winningJeeberId,
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };

    private static WebApplicationFactory<Program> NewFactory(
        IOfferServiceClient fakeOffer, IConversationProvisioner fakeChat)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fakeOffer);
                    services.RemoveAll<IConversationProvisioner>();
                    services.AddSingleton(fakeChat);
                });
            });

    private static async Task<string> SeedRequestAsync(
        WebApplicationFactory<Program> factory, string clientId, string? conversationId = null)
    {
        var requests = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await requests.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package"
        }, default);
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            created.ConversationId = conversationId;
        }
        return created.Id;
    }

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    /// <summary>Records the post-accept conversation-advance call for assertions.</summary>
    private sealed class RecordingConversationProvisioner : IConversationProvisioner
    {
        public int AdvanceCallCount { get; private set; }
        public string? LastConversationId { get; private set; }
        public string? LastWinningJeeberId { get; private set; }
        public IReadOnlyList<string>? LastLosingMemberIds { get; private set; }

        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<string?> AdvanceToAcceptedAsync(
            string? conversationId, string winningJeeberId,
            IReadOnlyList<string> losingMemberIds, CancellationToken ct)
        {
            AdvanceCallCount++;
            LastConversationId = conversationId;
            LastWinningJeeberId = winningJeeberId;
            LastLosingMemberIds = losingMemberIds;
            return Task.FromResult<string?>("winner-member-id");
        }
    }

    /// <summary>Simulates a chat-service blip during the accept advance.</summary>
    private sealed class ThrowingConversationProvisioner : IConversationProvisioner
    {
        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<string?> AdvanceToAcceptedAsync(
            string? conversationId, string winningJeeberId,
            IReadOnlyList<string> losingMemberIds, CancellationToken ct)
            => throw new HttpRequestException("chat-service unavailable");
    }

    /// <summary>
    /// Test double for the offer-service typed client — returns a canned
    /// <see cref="OfferAcceptResult"/>. Only the accept-with-status path is exercised.
    /// </summary>
    private sealed class FakeOfferServiceClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Result);

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
