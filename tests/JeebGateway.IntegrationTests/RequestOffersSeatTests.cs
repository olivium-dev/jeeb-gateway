using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Conversations.Client;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 (B / H5,N3,N4,A6) — offer-submit SEATS the offering jeeber as a conversation
/// participant so a legitimately-seated jeeber reads the conversation 200 while a true
/// non-member still 403s.
///
/// <para>These tests pin the H5/N3/N4/A6 root-cause fix: the conversation can be created
/// by the client's SEPARATE <c>POST /v1/chat/jeeb/conversations</c> call (keyed by
/// correlation_key == requestId) WITHOUT the conversation id ever being written back onto
/// the gateway's request ledger row. Before the fix the seat block was skipped whenever
/// <c>request.ConversationId</c> was empty, so the jeeber was never added and 403'd. The
/// fix resolves the conversation by correlation key (== requestId) via chat-service before
/// seating. chat-service is the membership authority; the gateway only composes the
/// read + AddParticipant call.</para>
///
/// <para>Driven through the REAL host with a fake <see cref="IJeebConversationClient"/>
/// (chat-service stands in via a sequenced upstream PR), mirroring
/// <c>JeebConversationsBffTests</c>'s harness exactly.</para>
/// </summary>
public sealed class RequestOffersSeatTests
{
    [Fact]
    public async Task Submit_With_ConversationId_On_Ledger_Seats_Jeeber_Using_LedgerId()
    {
        // The order-create auto-create path stamped a conversation id on the request row.
        var fake = new FakeSeatConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var (clientId, requestId) = await SeedRequestWithConversationAsync(factory, "conv-on-ledger");

        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Seated by the LEDGER id (no correlation lookup needed) with the jeeber's id + role.
        fake.AddParticipantCalls.Should().Be(1);
        fake.LastAddParticipantConversationId.Should().Be("conv-on-ledger");
        fake.LastAddParticipant!.UserId.Should().Be(jeeberId);
        fake.LastAddParticipant.RoleInConvo.Should().Be("jeeber_offerer");
        fake.CorrelationLookups.Should().Be(0, "the ledger already carried the conversation id");
    }

    [Fact]
    public async Task Submit_Without_ConversationId_On_Ledger_Resolves_By_Correlation_Then_Seats()
    {
        // The client created the conversation via the SEPARATE H1 call: chat-service knows
        // it by correlation_key == requestId, but the gateway's request row has NO id.
        // THIS is the H5/N3/N4/A6 root-cause scenario.
        var fake = new FakeSeatConversationClient { ConversationIdForCorrelation = "conv-by-correlation" };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var (_, requestId) = await SeedRequestAsync(factory); // ConversationId left null

        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 15 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        // The gateway resolved the conversation by correlation key (== requestId) and seated.
        fake.CorrelationLookups.Should().Be(1);
        fake.LastCorrelationKey.Should().Be(requestId);
        fake.AddParticipantCalls.Should().Be(1);
        fake.LastAddParticipantConversationId.Should().Be("conv-by-correlation");
        fake.LastAddParticipant!.UserId.Should().Be(jeeberId);
        fake.LastAddParticipant.RoleInConvo.Should().Be("jeeber_offerer");
    }

    [Fact]
    public async Task Submit_When_Seat_Throws_Still_Returns_201_DegradeDontFail()
    {
        // chat-service is unreachable / the conversation does not exist yet: the seat MUST
        // NOT flip the committed offer 201 into a 5xx (degrade-don't-fail).
        var fake = new FakeSeatConversationClient
        {
            CorrelationThrows = new JeebConversationApiException(HttpStatusCode.NotFound, "no conversation"),
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, $"jeeber-{Guid.NewGuid()}").PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.AddParticipantCalls.Should().Be(0, "the correlation lookup faulted before any seat");
    }

    [Fact]
    public async Task Submit_With_Chat_Flag_Off_Does_Not_Seat()
    {
        // Flag off: no chat composition at all (pre-S08 behaviour preserved).
        var fake = new FakeSeatConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: false);
        var (_, requestId) = await SeedRequestWithConversationAsync(factory, "conv-x");

        var resp = await JeeberClient(factory, $"jeeber-{Guid.NewGuid()}").PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.AddParticipantCalls.Should().Be(0);
        fake.CorrelationLookups.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

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

    private static async Task<(string clientId, string requestId)> SeedRequestWithConversationAsync(
        WebApplicationFactory<Program> factory, string conversationId)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package",
        }, default);
        // Stamp a conversation id on the ledger row (order-create auto-create path). The
        // in-memory store returns the live row reference, so mutating ConversationId here
        // is reflected by the subsequent GetAsync the controller performs.
        var row = await store.GetAsync(created.Id, default);
        row!.ConversationId = conversationId;
        return (clientId, created.Id);
    }

    private static WebApplicationFactory<Program> MakeFactory(
        FakeSeatConversationClient fake, bool chatEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IJeebConversationClient>();
                services.AddSingleton<IJeebConversationClient>(fake);
                services.Configure<UpstreamFeatureFlags>(f => f.Chat = chatEnabled);
            });
        });

    /// <summary>
    /// Records add-participant + correlation-lookup calls so the tests assert the gateway
    /// (a) seats the jeeber with the correct id/role and (b) resolves the conversation by
    /// correlation key when the ledger row carries no id. Only the two methods the seat
    /// path uses are meaningful; the rest satisfy the interface.
    /// </summary>
    private sealed class FakeSeatConversationClient : IJeebConversationClient
    {
        public string? ConversationIdForCorrelation { get; init; }
        public JeebConversationApiException? CorrelationThrows { get; init; }

        public int CorrelationLookups { get; private set; }
        public string? LastCorrelationKey { get; private set; }

        public int AddParticipantCalls { get; private set; }
        public string? LastAddParticipantConversationId { get; private set; }
        public AddJeebParticipantRequest? LastAddParticipant { get; private set; }

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct)
        {
            CorrelationLookups++;
            LastCorrelationKey = correlationKey;
            if (CorrelationThrows is not null)
            {
                throw CorrelationThrows;
            }
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = ConversationIdForCorrelation ?? string.Empty,
                CorrelationKey = correlationKey,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });
        }

        public Task<JeebConversationResponse> GetConversationByIdAsync(
            string conversationId, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = conversationId,
                CorrelationKey = conversationId,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });

        public Task<JeebConversationParticipant> AddParticipantAsync(
            string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
        {
            AddParticipantCalls++;
            LastAddParticipantConversationId = conversationId;
            LastAddParticipant = request;
            return Task.FromResult(new JeebConversationParticipant
            {
                UserId = request.UserId,
                RoleInConvo = request.RoleInConvo,
                RemovedAt = null,
            });
        }

        // ---- unused by the seat path; minimal interface satisfaction ----
        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse());

        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
            => Task.FromResult(new JeebMessageResponse());

        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(new JeebMessageListResponse());

        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => Task.FromResult(new JeebMessageListResponse());

        public Task<JeebConversationMembership> GetMembershipAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(new JeebConversationMembership { IsMember = false });

        public Task<JeebConversationResponse> AdvancePhaseAsync(
            string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse());
    }
}
