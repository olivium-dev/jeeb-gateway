using System;
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
/// GW5 / W1.6-gateway — G3: the fault the reconciler actually exists for, which is a
/// GATEWAY BOUNCE between the accept commit and the chat settle.
///
/// <para>G2 injects a chat-service fault: the process stays up, the in-memory projection
/// stays warm, and the candidate is trivially findable. That is the easy half. The batch's
/// stated harness is "kills between commit and seat" — and a kill empties
/// <see cref="InMemoryRequestsStore"/> completely. If the candidate query reads the
/// in-memory projection, then after exactly the fault this reconciler was built for it
/// returns an empty page, the sweep reports "nothing to reconcile", every counter reads
/// clean, and the winner stays locked out of the only channel a cash handover has. A
/// green G2 cannot see any of that.</para>
///
/// <para>So these tests assert the durable read directly, and then run the sweep over a
/// store whose in-memory half is FRESH AND EMPTY — the post-restart shape — with the
/// evidence living only in the Postgres mirror. G3.7 is the complement that stops G3.6
/// being a tautology: same harness, mirror also empty, nothing healed.</para>
///
/// <para>Everything here is a HOST-side <c>suite</c> measurement against doubles. It
/// proves the candidate query and the sweep compose correctly across a simulated bounce.
/// It does NOT prove Postgres returns those rows — the SQL in
/// <c>PostgresDurableRequestsMirror.ListAssignedSinceAsync</c> is only exercisable against
/// a real database (Testcontainers, and <c>docker info</c> is down on this host), so that
/// remains NOT-PROVEN here and is called out in the pack report rather than papered over
/// with a fake that agrees with itself.</para>
/// </summary>
[Collection(Gw5ChatSettleCollection.Name)]
public class G3_ReconcileDurabilityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-01T09:00:00Z");
    private const string Owner = "client-owner";
    private const string Winner = "jeeber-win";

    // =====================================================================
    // The candidate query itself — DurableRequestsStore.ListAssignedSinceAsync
    // =====================================================================

    /// <summary>
    /// G3.1 — THE BOUNCE READ. The in-memory projection is empty (as it is on every
    /// restart) and the assignment survives only in the mirror. The candidate query must
    /// still return it.
    ///
    /// <para>This is the single assertion that separates a reconciler which heals a
    /// restart from one which only heals a chat blip. Break it — delegate to the inner
    /// store alone — and the sweep answers "0 candidates" for the exact fault it exists
    /// to repair, while looking perfectly healthy.</para>
    /// </summary>
    [Fact]
    public async Task ListAssignedSince_AfterABounce_ReturnsTheMirrorRow()
    {
        var inner = new InMemoryRequestsStore(new FakeTimeProvider(T0));   // post-restart: empty
        var mirror = new FakeMirror();
        mirror.Rows.Add(Row("11111111-1111-1111-1111-111111111111", Winner, T0));
        var store = NewDurableStore(inner, mirror);

        var candidates = await store.ListAssignedSinceAsync(T0.AddHours(-24), 50, CancellationToken.None);

        mirror.ListCalls.Should().Be(1, "the durable half must actually be asked");
        candidates.Select(r => r.Id).Should().ContainSingle()
            .Which.Should().Be("11111111-1111-1111-1111-111111111111");
        candidates.Single().JeeberId.Should().Be(Winner);
    }

    /// <summary>
    /// G3.2 — a mirror read FAULT degrades to the in-memory rows. It must never surface as
    /// an empty page, because an empty page is read by the sweep as "nothing to
    /// reconcile" — a Postgres blip would silently mean "everything is fine".
    /// </summary>
    [Fact]
    public async Task ListAssignedSince_WhenTheMirrorFaults_DegradesToInMemory_NotToEmpty()
    {
        var clock = new FakeTimeProvider(T0);
        var inner = new InMemoryRequestsStore(clock);
        var warm = await SeedAssignedAsync(inner, Winner);
        var mirror = new FakeMirror { Throw = true };
        var store = NewDurableStore(inner, mirror);

        var candidates = await store.ListAssignedSinceAsync(T0.AddHours(-24), 50, CancellationToken.None);

        candidates.Select(r => r.Id).Should().Equal(warm.Id);
    }

    /// <summary>
    /// G3.3 — the merge is a UNION keyed by id, and the in-memory row wins. The mirror
    /// carries only the columns the mirror has; the live row carries the full field set
    /// (status, conversation id, fee), so a duplicate must not downgrade a warm row — and
    /// must not be counted twice, which would settle the same conversation twice per
    /// sweep.
    /// </summary>
    [Fact]
    public async Task ListAssignedSince_MergesById_PreferringTheLiveRow()
    {
        var clock = new FakeTimeProvider(T0);
        var inner = new InMemoryRequestsStore(clock);
        var warm = await SeedAssignedAsync(inner, Winner);
        await inner.SetConversationIdAsync(warm.Id, "conv-warm", CancellationToken.None);

        var mirror = new FakeMirror();
        mirror.Rows.Add(Row(warm.Id, Winner, T0));                                   // same id, stale
        mirror.Rows.Add(Row("22222222-2222-2222-2222-222222222222", Winner, T0));    // mirror-only

        var store = NewDurableStore(inner, mirror);

        var candidates = await store.ListAssignedSinceAsync(T0.AddHours(-24), 50, CancellationToken.None);

        candidates.Should().HaveCount(2, "one union, not one row counted twice");
        candidates.Single(r => r.Id == warm.Id).ConversationId
            .Should().Be("conv-warm", "the live row wins the merge");
    }

    /// <summary>
    /// G3.4 — the page cap is applied AFTER the merge. Applied before, a page made
    /// entirely of rows the in-memory store also holds would shrink to nothing useful and
    /// the backlog would drain far more slowly than PageSize suggests.
    /// </summary>
    [Fact]
    public async Task ListAssignedSince_AppliesTheLimitAfterTheMerge()
    {
        var inner = new InMemoryRequestsStore(new FakeTimeProvider(T0));
        var mirror = new FakeMirror();
        for (var i = 0; i < 5; i++)
        {
            mirror.Rows.Add(Row($"3333333{i}-3333-3333-3333-333333333333", Winner, T0.AddMinutes(-i)));
        }
        var store = NewDurableStore(inner, mirror);

        var page = await store.ListAssignedSinceAsync(T0.AddHours(-24), 3, CancellationToken.None);

        page.Should().HaveCount(3);
        page.Select(r => r.CreatedAt).Should().BeInDescendingOrder("newest-first, like the mirror SQL");
    }

    /// <summary>
    /// G3.5 — a non-positive limit is an empty page, not an unbounded scan. Cheap, but
    /// this method runs on a timer against a shared service.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListAssignedSince_WithANonPositiveLimit_ReadsNothing(int limit)
    {
        var mirror = new FakeMirror();
        mirror.Rows.Add(Row("44444444-4444-4444-4444-444444444444", Winner, T0));
        var store = NewDurableStore(new InMemoryRequestsStore(new FakeTimeProvider(T0)), mirror);

        (await store.ListAssignedSinceAsync(T0.AddHours(-24), limit, CancellationToken.None))
            .Should().BeEmpty();
        mirror.ListCalls.Should().Be(0);
    }

    /// <summary>
    /// G3.6 — POSITIVE control for the whole "no mirror configured" degradation: with no
    /// Postgres wired the answer is exactly the in-memory list, i.e. today's behaviour.
    /// </summary>
    [Fact]
    public async Task ListAssignedSince_WithNoMirrorWired_IsExactlyTheInMemoryList()
    {
        var inner = new InMemoryRequestsStore(new FakeTimeProvider(T0));
        var warm = await SeedAssignedAsync(inner, Winner);
        var store = NewDurableStore(inner, mirror: null);

        (await store.ListAssignedSinceAsync(T0.AddHours(-24), 50, CancellationToken.None))
            .Select(r => r.Id).Should().Equal(warm.Id);
    }

    /// <summary>
    /// G3.7 — the in-memory candidate filter itself: only rows WITH an assigned jeeber,
    /// only rows inside the window. Asserted on <see cref="InMemoryRequestsStore"/>
    /// directly because it is the implementation every non-durable deployment uses.
    /// </summary>
    [Fact]
    public async Task InMemoryListAssignedSince_FiltersOnAssignmentAndWindow()
    {
        var clock = new FakeTimeProvider(T0);
        var inner = new InMemoryRequestsStore(clock);

        var unassigned = await SeedAssignedAsync(inner, jeeberId: null);
        var assigned = await SeedAssignedAsync(inner, Winner);
        clock.Advance(TimeSpan.FromHours(3));
        var recent = await SeedAssignedAsync(inner, Winner);

        var window = await inner.ListAssignedSinceAsync(T0.AddHours(2), 50, CancellationToken.None);

        window.Select(r => r.Id).Should().Equal(recent.Id);
        window.Select(r => r.Id).Should().NotContain(assigned.Id, "outside the look-back");
        window.Select(r => r.Id).Should().NotContain(unassigned.Id, "no jeeber to settle onto");
    }

    // =====================================================================
    // The bounce, end to end
    // =====================================================================

    /// <summary>
    /// G3.8 — THE FAULT INJECTION THE BATCH ASKS FOR: killed between commit and seat.
    ///
    /// <para>The accept committed and the projection wrote the assignment; the process
    /// then died before the settle. We model the restart honestly — a brand new, EMPTY
    /// <see cref="InMemoryRequestsStore"/>, a chat-service that has never heard of this
    /// conversation, and the assignment surviving ONLY in the durable mirror. Nobody hands
    /// the reconciler an id. It re-derives the candidate, finds chat-service divergent
    /// (404: no conversation at all), creates it, and settles it onto the winner.</para>
    /// </summary>
    [Fact]
    public async Task Sweep_AfterAGatewayBounce_RederivesTheCandidateFromTheMirror_AndHeals()
    {
        var h = new BounceHarness();
        h.Mirror.Rows.Add(Row("55555555-5555-5555-5555-555555555555", Winner, T0));

        var healed = await h.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(1);
        h.Chat.Settles.Should().ContainSingle();
        var settle = h.Chat.Settles.Single();
        settle.WinnerUserId.Should().Be(Winner);
        settle.Phase.Should().Be("accepted");
        settle.WinnerRoleInConvo.Should().Be("jeeber_winner");
        settle.RemoveOthers.Should().BeTrue("a settled thread is owner + winner and nobody else");

        // And the healed conversation id was written back through the DURABLE store, so
        // the next bounce does not repeat the resolve-or-create.
        h.Mirror.ConversationIds.Should()
            .ContainKey("55555555-5555-5555-5555-555555555555");
    }

    /// <summary>
    /// G3.9 — the complement, and the reason G3.8 is a measurement rather than a
    /// tautology: identical harness, identical wiring, nothing in the mirror. If the sweep
    /// still healed something it would be inventing candidates, and G3.8 would be proving
    /// nothing about where the row came from.
    /// </summary>
    [Fact]
    public async Task Sweep_AfterABounce_WithAnEmptyMirror_HealsNothing()
    {
        var h = new BounceHarness();

        (await h.Reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        h.Chat.Settles.Should().BeEmpty();
        h.Chat.Creates.Should().BeEmpty();
    }

    /// <summary>
    /// G3.10 — a mirror blip during a sweep must not be read as "all clear". The sweep
    /// degrades to the (empty, post-bounce) in-memory page and heals nothing, rather than
    /// throwing the whole sweep away — and the very next sweep, with the mirror back,
    /// heals. Same harness, so the second half is the positive control for the first.
    /// </summary>
    [Fact]
    public async Task Sweep_WhenTheMirrorIsDown_HealsNothingThenHealsOnRecovery()
    {
        var h = new BounceHarness();
        h.Mirror.Rows.Add(Row("66666666-6666-6666-6666-666666666666", Winner, T0));

        h.Mirror.Throw = true;
        (await h.Reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        h.Chat.Settles.Should().BeEmpty();

        h.Mirror.Throw = false;
        (await h.Reconciler.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        h.Chat.Settles.Should().ContainSingle();
    }

    // =====================================================================
    // helpers
    // =====================================================================

    private static DeliveryRequest Row(string id, string? jeeberId, DateTimeOffset createdAt) => new()
    {
        Id = id,
        ClientId = Owner,
        Status = RequestStatus.Accepted,
        Description = "Pick up the package",
        CreatedAt = createdAt,
        JeeberId = jeeberId,
    };

    private static async Task<DeliveryRequest> SeedAssignedAsync(IRequestsStore store, string? jeeberId)
    {
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = Owner,
            Description = "Pick up the package",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = 33.5138, Lng = 36.2765 },
            DropoffLocation = new GeoPoint { Lat = 33.52, Lng = 36.28 },
        }, CancellationToken.None);

        await store.SetStatusAsync(created.Id, RequestStatus.Accepted, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(jeeberId))
        {
            await store.SetJeeberIdAsync(created.Id, jeeberId, CancellationToken.None);
        }

        return (await store.GetAsync(created.Id, CancellationToken.None))!;
    }

    /// <summary>
    /// <see cref="DurableRequestsStore"/> with only the two collaborators the candidate
    /// query and the conversation-id stamp touch. The delivery client / bundle recorder /
    /// provisioner / broadcast recorder are create-path collaborators and are deliberately
    /// left null: if a future edit makes either of the two paths under test depend on one,
    /// these tests must fail loudly rather than pass against a stub that quietly absorbs
    /// the new call.
    /// </summary>
    private static DurableRequestsStore NewDurableStore(IRequestsStore inner, FakeMirror? mirror)
        => new(
            inner,
            delivery: null!,
            bundles: null!,
            conversations: null!,
            broadcasts: null!,
            Options.Create(new DurableRequestsOptions { Enabled = true }),
            NullLogger<DurableRequestsStore>.Instance,
            mirror);

    /// <summary>
    /// A gateway that has just restarted: empty in-memory projection, a mirror that
    /// survived, and a chat-service that has never heard of the conversation.
    /// </summary>
    private sealed class BounceHarness
    {
        public FakeMirror Mirror { get; } = new();
        public MinimalChat Chat { get; } = new();
        public DurableRequestsStore Store { get; }
        public AcceptChatSettleReconciler Reconciler { get; }

        public BounceHarness()
        {
            var clock = new FakeTimeProvider(T0);
            Store = NewDurableStore(new InMemoryRequestsStore(clock), Mirror);

            var flags = Options.Create(new UpstreamFeatureFlags { Chat = true });
            var settler = new AcceptChatSettler(
                Chat, Store, flags, NullLogger<AcceptChatSettler>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton<IRequestsStore>(Store);
            services.AddSingleton<IJeebConversationClient>(Chat);
            services.AddSingleton<IAcceptChatSettler>(settler);

            Reconciler = new AcceptChatSettleReconciler(
                services.BuildServiceProvider(),
                clock,
                Options.Create(new AcceptChatSettleReconcilerOptions
                {
                    LookBack = TimeSpan.FromHours(24),
                    PageSize = 50,
                }),
                flags,
                NullLogger<AcceptChatSettleReconciler>.Instance);
        }
    }

    /// <summary>
    /// A chat-service double written INDEPENDENTLY of G2's. It knows only three verbs and
    /// starts empty, so a candidate reaches it as a 404 — the post-bounce shape. The two
    /// pre-GW5 verbs throw: if the settle path ever regrows the two-call sequence these
    /// tests say so instead of quietly recording it.
    /// </summary>
    private sealed class MinimalChat : IJeebConversationClient
    {
        private readonly Dictionary<string, string> _idByCorrelation = new(StringComparer.Ordinal);
        private int _seq;

        public List<CreateJeebConversationRequest> Creates { get; } = new();
        public List<SettleJeebConversationRequest> Settles { get; } = new();

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(string correlationKey, CancellationToken ct)
            => _idByCorrelation.TryGetValue(correlationKey, out var id)
                ? Task.FromResult(new JeebConversationResponse
                {
                    ConversationId = id,
                    CorrelationKey = correlationKey,
                    Phase = "broadcasting",
                    Participants = new List<JeebConversationParticipant>(),
                })
                : throw new JeebConversationApiException(HttpStatusCode.NotFound, null);

        public Task<JeebConversationResponse> CreateConversationAsync(CreateJeebConversationRequest request, CancellationToken ct)
        {
            Creates.Add(request);
            if (!_idByCorrelation.TryGetValue(request.RequestId, out var id))
            {
                id = $"conv-{++_seq}";
                _idByCorrelation[request.RequestId] = id;
            }
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = id,
                CorrelationKey = request.RequestId,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>
                {
                    new() { UserId = request.ClientUserId, RoleInConvo = "client" },
                },
            });
        }

        public Task<JeebConversationSettleResponse> SettleAsync(
            string conversationId, SettleJeebConversationRequest request, CancellationToken ct)
        {
            Settles.Add(request);
            return Task.FromResult(new JeebConversationSettleResponse
            {
                Conversation = new JeebConversationResponse
                {
                    ConversationId = conversationId,
                    Phase = request.Phase,
                    Participants = new List<JeebConversationParticipant>
                    {
                        new() { UserId = Owner, RoleInConvo = "client" },
                        new() { UserId = request.WinnerUserId, RoleInConvo = request.WinnerRoleInConvo },
                    },
                },
                Seated = true,
                PhaseChanged = true,
            });
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
    }

    /// <summary>
    /// The gateway-Postgres mirror double. <see cref="Rows"/> is what survived the bounce.
    /// </summary>
    private sealed class FakeMirror : IDurableRequestsMirror
    {
        public List<DeliveryRequest> Rows { get; } = new();
        public Dictionary<string, string> ConversationIds { get; } = new(StringComparer.Ordinal);
        public bool Throw { get; set; }
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
            DateTimeOffset since, int limit, CancellationToken ct)
        {
            ListCalls++;
            if (Throw) throw new InvalidOperationException("postgres down");
            IReadOnlyList<DeliveryRequest> page = Rows
                .Where(r => !string.IsNullOrWhiteSpace(r.JeeberId) && r.CreatedAt >= since)
                .OrderByDescending(r => r.CreatedAt)
                .Take(limit)
                .ToArray();
            return Task.FromResult(page);
        }

        public Task UpdateConversationIdAsync(string requestId, string conversationId, CancellationToken ct)
        {
            ConversationIds[requestId] = conversationId;
            return Task.CompletedTask;
        }

        public Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct) => Task.CompletedTask;
        public Task MarkCancelledAsync(string requestId, string gwStatus, string? cancelledBy, string? cancellationReason, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> MarkExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct) => Task.FromResult(false);
        public Task UpdateLifecycleAsync(string requestId, string? gwStatus, string? gwJeeberId, decimal? gwAcceptedFee, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.Id == requestId));
        public Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => Task.FromResult(Rows.FirstOrDefault(r => r.ConversationId == conversationId));
    }
}
