using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw5Pack;

/// <summary>
/// GW5 / W1.6-gateway — G2: the RECONCILER, which is the half of the fix that the
/// one-call settle cannot cover on its own.
///
/// <para>Folding seat + phase + loser-removal into one request removes the window
/// BETWEEN two chat writes. It does not remove the window between the accept saga's
/// commit and that request: the gateway can be killed, or chat-service can be down, and
/// the attempt is simply lost. The pre-GW5 code's own catch logged <i>"may read 403 on
/// chat until reconciled"</i> and nothing reconciled. These tests are the proof that
/// something now does.</para>
///
/// <para>Every case is stated as a PAIR — a divergent state that MUST be healed, and the
/// matching settled state that must be left alone. A reconciler that re-settled
/// everything unconditionally would pass every "healed" assertion on its own and be a
/// permanent load generator against a shared service.</para>
/// </summary>
public class G2_AcceptSettleReconcileTests
{
    private const string Owner = "client-owner";
    private const string Winner = "jeeber-win";
    private const string Loser = "jeeber-lost";

    /// <summary>
    /// G2.1 — THE FAULT INJECTION. chat-service is dead when the accept settles, exactly
    /// as if the gateway had been killed between the commit and the seat. The attempt is
    /// lost; the request row is not. One sweep later, the conversation is settled.
    ///
    /// <para>Note what makes this a real test rather than a tautology: the harness never
    /// tells the reconciler which request to heal. It re-derives the candidate from the
    /// durable request row — the assignment the accept projection wrote BEFORE the chat
    /// step ran.</para>
    /// </summary>
    [Fact]
    public async Task Sweep_HealsAnAcceptWhoseInlineSettleWasLost()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var request = await harness.SeedAcceptedRequestAsync();

        // The inline attempt: chat-service is down, so it raises and is swallowed by the
        // accept path's degrade-don't-fail block (asserted in S03AcceptConversationSeatTests).
        chat.Fail = true;
        var inline = async () => await harness.Settler.SettleAsync(request, Winner, CancellationToken.None);
        await inline.Should().ThrowAsync<JeebConversationApiException>();
        chat.Settles.Should().BeEmpty("nothing landed — that is the fault being injected");

        // chat-service comes back. Nobody told the reconciler anything.
        chat.Fail = false;
        var healed = await harness.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(1);
        chat.Settles.Should().ContainSingle();
        var settle = chat.Settles.Single();
        settle.WinnerUserId.Should().Be(Winner);
        settle.Phase.Should().Be("accepted");
        settle.RemoveOthers.Should().BeTrue();
    }

    /// <summary>
    /// G2.2 — THE NEGATIVE CONTROL for G2.1, and the one that stops this class being
    /// theatre. Same harness, same candidate, but chat-service already reports the
    /// settled 1:1 — the sweep must settle NOTHING. Without this, a reconciler that
    /// blindly re-settled every accepted request would pass G2.1 and hammer a shared
    /// service forever.
    /// </summary>
    [Fact]
    public async Task Sweep_WhenChatAlreadyAgreesItIsSettled_DoesNothing()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var request = await harness.SeedAcceptedRequestAsync();

        await harness.Settler.SettleAsync(request, Winner, CancellationToken.None);
        chat.Settles.Should().ContainSingle("positive control: the inline settle DID land");
        chat.Settles.Clear();

        var healed = await harness.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(0);
        chat.Settles.Should().BeEmpty();
    }

    /// <summary>
    /// G2.3 — divergence is decided by the AUTHORITY's roster, not by a gateway-local
    /// flag. Each row here is a state chat-service can genuinely be left in by a partial
    /// pre-GW5 sequence, and each must be re-settled.
    ///
    /// <list type="bullet">
    ///   <item><c>broadcasting</c> — the seat landed, the phase advance did not. THE
    ///   defect GW5 exists to remove.</item>
    ///   <item>winner soft-removed — the winner is listed but inactive, so they read
    ///   403.</item>
    ///   <item>a losing bidder still active — simultaneously a leak risk and the reason
    ///   the winner reads a blank thread (chat-service only grants cross-visibility in a
    ///   clean 1:1).</item>
    ///   <item>no conversation at all — the ensure never landed.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("broadcasting", true, false, false)]
    [InlineData("accepted", false, false, false)]
    [InlineData("accepted", true, true, false)]
    [InlineData("accepted", true, false, true)]
    public async Task Sweep_HealsEveryDivergentRosterShape(
        string phase, bool winnerSeated, bool loserStillActive, bool noConversation)
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var request = await harness.SeedAcceptedRequestAsync();

        if (!noConversation)
        {
            chat.Seed(request.Id, phase, winnerSeated, loserStillActive, Owner);
        }

        var healed = await harness.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(1);
        chat.Settles.Should().ContainSingle();
    }

    /// <summary>
    /// G2.4 — the clean 1:1 is NOT divergent. Owner + winner active, winner carrying the
    /// winner role, phase settled. The exact complement of G2.3.
    /// </summary>
    [Fact]
    public async Task Sweep_LeavesACleanOneToOneAlone()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var request = await harness.SeedAcceptedRequestAsync();
        chat.Seed(request.Id, "accepted", winnerSeated: true, loserStillActive: false, ownerId: Owner);

        (await harness.Reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        chat.Settles.Should().BeEmpty();
    }

    /// <summary>
    /// G2.5 — a still-broken row must not wedge the sweep. Two divergent candidates, the
    /// first one permanently failing: the second is still healed.
    /// </summary>
    [Fact]
    public async Task Sweep_IsolatesAFailingRow_AndKeepsGoing()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var poisoned = await harness.SeedAcceptedRequestAsync();
        var healthy = await harness.SeedAcceptedRequestAsync();

        chat.FailForCorrelation.Add(poisoned.Id);

        var healed = await harness.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(1);
        chat.Settles.Should().ContainSingle().Which.CorrelationKey.Should().Be(healthy.Id);
    }

    /// <summary>
    /// G2.6 — a request with NO assigned jeeber is not a candidate. There is nothing to
    /// settle onto, and sweeping it would mean settling every open auction.
    /// </summary>
    [Fact]
    public async Task Sweep_IgnoresRequestsWithNoAssignedJeeber()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        await harness.SeedRequestAsync(jeeberId: null);

        (await harness.Reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        chat.Settles.Should().BeEmpty();
    }

    /// <summary>
    /// G2.7 — the look-back bounds the sweep. A row created before the window is not a
    /// candidate, so a permanently unsettleable request ages out instead of being
    /// retried until the end of time.
    /// </summary>
    [Fact]
    public async Task Sweep_IgnoresRequestsOlderThanTheLookBack()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat, lookBack: TimeSpan.FromHours(1));
        await harness.SeedAcceptedRequestAsync();

        harness.Clock.Advance(TimeSpan.FromHours(2));

        (await harness.Reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        chat.Settles.Should().BeEmpty();
    }

    /// <summary>
    /// G2.8 — the settle is CONVERGENT, so a replay is safe and is the intended recovery
    /// action. Two identical settles leave chat-service in the same end state and the
    /// second reports <c>already_settled</c> — which the gateway records but never
    /// branches on, because chat-service reconciles its direct-read projection on EVERY
    /// settle including that replay.
    /// </summary>
    [Fact]
    public async Task Settle_ReplayIsIdempotent_AndReportsAlreadySettled()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var request = await harness.SeedAcceptedRequestAsync();

        var first = await harness.Settler.SettleAsync(request, Winner, CancellationToken.None);
        var second = await harness.Settler.SettleAsync(request, Winner, CancellationToken.None);

        first.Status.Should().Be(AcceptChatSettleStatus.Settled);
        second.Status.Should().Be(AcceptChatSettleStatus.Settled);
        first.AlreadySettled.Should().BeFalse();
        second.AlreadySettled.Should().BeTrue();
        first.ConversationId.Should().Be(second.ConversationId);

        // Both calls reached chat-service — a replay that short-circuited locally would
        // skip the projection repair the replay exists to perform.
        chat.Settles.Should().HaveCount(2);
        chat.Roster(request.Id).Should().BeEquivalentTo(new[] { Owner, Winner });
    }

    /// <summary>
    /// G2.9 — with the Chat upstream flag off the settler touches chat-service at all,
    /// and reports Skipped rather than pretending it settled something.
    /// </summary>
    [Fact]
    public async Task Settle_WhenChatFlagOff_IsSkipped_AndTouchesNothing()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat, chatFlag: false);
        var request = await harness.SeedAcceptedRequestAsync();

        var result = await harness.Settler.SettleAsync(request, Winner, CancellationToken.None);

        result.Status.Should().Be(AcceptChatSettleStatus.Skipped);
        chat.Settles.Should().BeEmpty();
        chat.Creates.Should().BeEmpty();
    }

    /// <summary>
    /// G2.10 — the settler persists the resolved conversation id THROUGH the store, not
    /// merely onto the in-memory object. JEBV4-345: a bare field assignment leaves
    /// <c>gw_conversation_id</c> NULL in Postgres, and after a bounce the conversation
    /// can no longer be resolved back to the order — the chat push for it then dies
    /// silently.
    ///
    /// <para>The settler is handed a DETACHED copy of the row on purpose.
    /// <c>InMemoryRequestsStore.GetAsync</c> returns the live object, so passing the
    /// stored instance would let the bare field assignment alone satisfy this assertion
    /// and the test could not tell the two apart. With a detached copy the stored row can
    /// only change via <c>SetConversationIdAsync</c>.</para>
    /// </summary>
    [Fact]
    public async Task Settle_PersistsTheConversationIdThroughTheStore_NotJustOnTheObject()
    {
        var chat = new FakeChat();
        var harness = new Harness(chat);
        var stored = await harness.SeedAcceptedRequestAsync();

        var detached = new DeliveryRequest
        {
            Id = stored.Id,
            ClientId = stored.ClientId,
            Status = stored.Status,
            Description = stored.Description,
            CreatedAt = stored.CreatedAt,
        };

        var result = await harness.Settler.SettleAsync(detached, Winner, CancellationToken.None);

        result.ConversationId.Should().NotBeNullOrWhiteSpace();
        var reread = await harness.Requests.GetAsync(stored.Id, CancellationToken.None);
        reread!.ConversationId.Should().Be(result.ConversationId);
    }

    // =====================================================================
    // harness
    // =====================================================================

    private sealed class Harness
    {
        public FakeTimeProvider Clock { get; }
        public IRequestsStore Requests { get; }
        public IAcceptChatSettler Settler { get; }
        public AcceptChatSettleReconciler Reconciler { get; }

        public Harness(FakeChat chat, bool chatFlag = true, TimeSpan? lookBack = null)
        {
            Clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T09:00:00Z"));
            Requests = new InMemoryRequestsStore(Clock);

            var flags = Options.Create(new UpstreamFeatureFlags { Chat = chatFlag });
            Settler = new AcceptChatSettler(
                chat, Requests, flags, NullLogger<AcceptChatSettler>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton(Requests);
            services.AddSingleton<IJeebConversationClient>(chat);
            services.AddSingleton(Settler);

            Reconciler = new AcceptChatSettleReconciler(
                services.BuildServiceProvider(),
                Clock,
                Options.Create(new AcceptChatSettleReconcilerOptions
                {
                    LookBack = lookBack ?? TimeSpan.FromHours(24),
                    PageSize = 50,
                }),
                flags,
                NullLogger<AcceptChatSettleReconciler>.Instance);
        }

        public Task<DeliveryRequest> SeedAcceptedRequestAsync() => SeedRequestAsync(Winner);

        public async Task<DeliveryRequest> SeedRequestAsync(string? jeeberId)
        {
            var created = await Requests.CreateAsync(new CreateRequestInput
            {
                ClientId = Owner,
                Description = "Pick up the package",
                TierId = "flash",
                PickupLocation = new GeoPoint { Lat = 33.5138, Lng = 36.2765 },
                DropoffLocation = new GeoPoint { Lat = 33.52, Lng = 36.28 },
            }, CancellationToken.None);

            // Exactly what BuildAcceptedResponseAsync writes BEFORE the chat step runs —
            // which is why a process killed inside that step still leaves a candidate.
            await Requests.SetStatusAsync(created.Id, RequestStatus.Accepted, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(jeeberId))
            {
                await Requests.SetJeeberIdAsync(created.Id, jeeberId, CancellationToken.None);
            }

            return (await Requests.GetAsync(created.Id, CancellationToken.None))!;
        }
    }

    /// <summary>
    /// A chat-service double that models the ROSTER, not just the call. Asserting "settle
    /// was called" proves nothing about convergence; this one actually applies the end
    /// state, so a replay can be observed collapsing onto the same roster and
    /// <c>already_settled</c> is computed rather than hard-coded.
    /// </summary>
    private sealed class FakeChat : IJeebConversationClient
    {
        private readonly Dictionary<string, Convo> _byCorrelation = new(StringComparer.Ordinal);
        private int _seq;

        public bool Fail { get; set; }
        public HashSet<string> FailForCorrelation { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<CreateJeebConversationRequest> Creates { get; } = new();
        public List<SettleRecord> Settles { get; } = new();

        public IReadOnlyList<string> Roster(string correlationKey)
            => _byCorrelation.TryGetValue(correlationKey, out var c)
                ? c.Participants.Where(p => p.RemovedAt is null).Select(p => p.UserId).OrderBy(x => x).ToArray()
                : Array.Empty<string>();

        /// <summary>Put chat-service into a specific roster/phase state.</summary>
        public void Seed(string correlationKey, string phase, bool winnerSeated, bool loserStillActive, string ownerId)
        {
            var convo = new Convo($"conv-{++_seq}", correlationKey) { Phase = phase };
            convo.Participants.Add(new JeebConversationParticipant { UserId = ownerId, RoleInConvo = "client" });
            if (winnerSeated)
            {
                convo.Participants.Add(new JeebConversationParticipant
                {
                    UserId = Winner,
                    RoleInConvo = "jeeber_winner",
                });
            }
            if (loserStillActive)
            {
                convo.Participants.Add(new JeebConversationParticipant
                {
                    UserId = Loser,
                    RoleInConvo = "jeeber_offerer",
                });
            }
            _byCorrelation[correlationKey] = convo;
        }

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(string correlationKey, CancellationToken ct)
        {
            ThrowIfFaulted(correlationKey);
            if (!_byCorrelation.TryGetValue(correlationKey, out var convo))
                throw new JeebConversationApiException(HttpStatusCode.NotFound, null);
            return Task.FromResult(convo.ToResponse());
        }

        public Task<JeebConversationResponse> CreateConversationAsync(CreateJeebConversationRequest request, CancellationToken ct)
        {
            ThrowIfFaulted(request.RequestId);
            Creates.Enqueue(request);
            if (!_byCorrelation.TryGetValue(request.RequestId, out var convo))
            {
                convo = new Convo($"conv-{++_seq}", request.RequestId) { Phase = request.Phase };
                convo.Participants.Add(new JeebConversationParticipant
                {
                    UserId = request.ClientUserId,
                    RoleInConvo = request.OwnerRoleInConvo,
                });
                _byCorrelation[request.RequestId] = convo;
            }
            return Task.FromResult(convo.ToResponse());
        }

        public Task<JeebConversationSettleResponse> SettleAsync(
            string conversationId, SettleJeebConversationRequest request, CancellationToken ct)
        {
            var convo = _byCorrelation.Values.FirstOrDefault(c =>
                string.Equals(c.ConversationId, conversationId, StringComparison.Ordinal))
                ?? throw new JeebConversationApiException(HttpStatusCode.NotFound, null);

            ThrowIfFaulted(convo.CorrelationKey);
            Settles.Add(new SettleRecord(
                conversationId, convo.CorrelationKey, request.Phase, request.WinnerUserId,
                request.WinnerRoleInConvo, request.RemoveOthers));

            var changed = false;

            if (!string.Equals(convo.Phase, request.Phase, StringComparison.Ordinal))
            {
                convo.Phase = request.Phase;
                changed = true;
            }

            var winner = convo.Participants.FirstOrDefault(p =>
                string.Equals(p.UserId, request.WinnerUserId, StringComparison.Ordinal));
            if (winner is null)
            {
                convo.Participants.Add(new JeebConversationParticipant
                {
                    UserId = request.WinnerUserId,
                    RoleInConvo = request.WinnerRoleInConvo,
                });
                changed = true;
            }
            else
            {
                if (winner.RemovedAt is not null) { winner.RemovedAt = null; changed = true; }
                if (!string.Equals(winner.RoleInConvo, request.WinnerRoleInConvo, StringComparison.Ordinal))
                {
                    winner.RoleInConvo = request.WinnerRoleInConvo;
                    changed = true;
                }
            }

            var removed = new List<string>();
            if (request.RemoveOthers)
            {
                foreach (var p in convo.Participants)
                {
                    if (p.RemovedAt is not null) continue;
                    if (string.Equals(p.UserId, request.WinnerUserId, StringComparison.Ordinal)) continue;
                    if (string.Equals(p.RoleInConvo, "client", StringComparison.Ordinal)) continue;
                    p.RemovedAt = DateTimeOffset.UnixEpoch;
                    removed.Add(p.UserId);
                    changed = true;
                }
            }

            return Task.FromResult(new JeebConversationSettleResponse
            {
                Conversation = convo.ToResponse(),
                Seated = winner is null,
                PhaseChanged = changed,
                RemovedUserIds = removed,
                AlreadySettled = !changed,
            });
        }

        private void ThrowIfFaulted(string correlationKey)
        {
            if (Fail || FailForCorrelation.Contains(correlationKey))
                throw new JeebConversationApiException(HttpStatusCode.ServiceUnavailable, "chat-service unavailable");
        }

        public Task<JeebConversationParticipant> AddParticipantAsync(string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
            => throw new NotSupportedException("GW5: the post-accept path must not use the two-call sequence.");
        public Task<JeebConversationResponse> AdvancePhaseAsync(string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
            => throw new NotSupportedException("GW5: the post-accept path must not use the two-call sequence.");
        public Task<JeebMessageResponse> AppendMessageAsync(string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(string conversationId, string viewerUserId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebConversationMembership> GetMembershipAsync(string conversationId, string viewerUserId, CancellationToken ct)
            => throw new NotSupportedException();

        private sealed class Convo
        {
            public Convo(string conversationId, string correlationKey)
            {
                ConversationId = conversationId;
                CorrelationKey = correlationKey;
            }

            public string ConversationId { get; }
            public string CorrelationKey { get; }
            public string Phase { get; set; } = "broadcasting";
            public List<JeebConversationParticipant> Participants { get; } = new();

            public JeebConversationResponse ToResponse() => new()
            {
                ConversationId = ConversationId,
                CorrelationKey = CorrelationKey,
                Phase = Phase,
                Participants = Participants
                    .Select(p => new JeebConversationParticipant
                    {
                        UserId = p.UserId,
                        RoleInConvo = p.RoleInConvo,
                        RemovedAt = p.RemovedAt,
                    })
                    .ToList(),
            };
        }
    }

    private sealed record SettleRecord(
        string ConversationId, string CorrelationKey, string Phase,
        string WinnerUserId, string WinnerRoleInConvo, bool RemoveOthers);
}
