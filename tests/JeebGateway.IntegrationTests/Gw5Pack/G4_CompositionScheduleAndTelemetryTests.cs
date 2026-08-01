using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw5Pack;

/// <summary>
/// GW5 / W1.6-gateway — G4: is the thing actually TURNED ON, does it run when it should,
/// and does it emit a signal when it fails.
///
/// <para><b>Why this file exists at all.</b> G1–G3 all construct the settler and the
/// reconciler by hand. Every one of them passes with `AddScoped&lt;IAcceptChatSettler&gt;`
/// and `AddHostedService` deleted from <c>Program.cs</c> — the classes still compile and
/// still behave, they are simply never reached by the running gateway. This programme has
/// already shipped one 473-test green that stayed green with the feature entirely off, so
/// "the unit works" is not the claim that matters; "the composed application resolves it
/// and schedules it" is.</para>
///
/// <para>The telemetry half is the other invisible-failure guard. The defect GW5 removes
/// was invisible because a post-commit catch logged one warning and swallowed. Counters
/// are the fix for that, and a counter nobody ever asserted is exactly as good as the log
/// line it replaced.</para>
/// </summary>
[Collection(Gw5ChatSettleCollection.Name)]
public class G4_CompositionScheduleAndTelemetryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-01T09:00:00Z");
    private const string Owner = "client-owner";
    private const string Winner = "jeeber-win";

    // =====================================================================
    // composition — the running gateway, not a hand-built object graph
    // =====================================================================

    /// <summary>
    /// G4.1 — the real host resolves <see cref="IAcceptChatSettler"/>, and resolves it
    /// SCOPED. The lifetime is load-bearing, not a style choice: the settler holds the
    /// typed <see cref="IJeebConversationClient"/>, and a singleton capturing a typed
    /// HttpClient outlives its handler rotation.
    /// </summary>
    [Fact]
    public void RealHost_ResolvesTheSettler_Scoped()
    {
        using var factory = NewFactory();

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();

        var a = scopeA.ServiceProvider.GetService<IAcceptChatSettler>();
        var b = scopeB.ServiceProvider.GetService<IAcceptChatSettler>();

        a.Should().BeOfType<AcceptChatSettler>("the accept controller depends on this abstraction");
        b.Should().NotBeSameAs(a, "scoped — a singleton would outlive the typed chat client");
        scopeA.ServiceProvider.GetRequiredService<IAcceptChatSettler>()
            .Should().BeSameAs(a, "…but one instance per scope");
    }

    /// <summary>
    /// G4.2 — the reconciler is actually SCHEDULED by the running gateway. Registered as a
    /// concrete singleton and surfaced through <see cref="IHostedService"/>: without the
    /// hosted registration nothing ever sweeps and every heal test in this pack is
    /// describing code that production never executes.
    /// </summary>
    [Fact]
    public void RealHost_SchedulesTheReconcilerAsAHostedService()
    {
        using var factory = NewFactory();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        hosted.OfType<AcceptChatSettleReconciler>().Should().ContainSingle(
            "the heal pass must be hosted, not merely registered");
        factory.Services.GetService<AcceptChatSettleReconciler>().Should().BeSameAs(
            hosted.OfType<AcceptChatSettleReconciler>().Single(),
            "one instance — the hosted registration must not build a second, unswept copy");
    }

    /// <summary>
    /// G4.3 — the sweep is ON with no appsettings change, and BOUNDED. A heal pass that
    /// ships defaulted-off is a heal pass that does not exist; a heal pass with no page cap
    /// is an unbounded scan against a shared service on a timer.
    /// </summary>
    [Fact]
    public void RealHost_BindsTheReconcilerOptions_EnabledAndBounded()
    {
        using var factory = NewFactory();

        var opts = factory.Services
            .GetRequiredService<IOptions<AcceptChatSettleReconcilerOptions>>().Value;

        opts.Enabled.Should().BeTrue("default-off would mean nothing is ever healed");
        opts.SweepInterval.Should().BeGreaterThan(TimeSpan.Zero);
        opts.LookBack.Should().BeGreaterThan(opts.SweepInterval);
        opts.PageSize.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// G4.4 — the counters are published on the meter the OTLP/Prometheus exporter is
    /// already wired to (<c>Program.cs</c> <c>.AddMeter(BusinessOutcomeTelemetry.MeterName)</c>).
    /// A counter on an unexported meter is incremented into a void, which is
    /// indistinguishable from the log line it replaced.
    /// </summary>
    [Fact]
    public void Counters_ArePublishedOnTheExportedMeter()
    {
        var published = new Dictionary<string, string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, _) =>
            {
                if (inst.Name.StartsWith("chat.accept_settle.", StringComparison.Ordinal))
                {
                    published[inst.Name] = inst.Meter.Name;
                }
            },
        };
        RuntimeHelpers.RunClassConstructor(typeof(ChatSettleTelemetry).TypeHandle);
        listener.Start();

        published.Keys.Should().BeEquivalentTo(new[]
        {
            "chat.accept_settle.settled",
            "chat.accept_settle.failures",
            "chat.accept_settle.reconcile_divergent",
            "chat.accept_settle.reconciled",
        });
        published.Values.Should().AllBe(BusinessOutcomeTelemetry.MeterName);
    }

    // =====================================================================
    // schedule — WHEN the sweep runs
    // =====================================================================

    /// <summary>
    /// G4.5 — DELAY FIRST. A divergent candidate is waiting before the service starts; the
    /// sweep must NOT fire at host startup, and must fire once an interval has elapsed.
    ///
    /// <para>Both halves are load-bearing. The second is the obvious one. The first is the
    /// one that keeps this whole pack honest: with a boot-time sweep, any
    /// <c>WebApplicationFactory</c> host with the Chat flag on races its own test's accept,
    /// and "exactly one settle" then passes or fails on thread-pool timing. A suite that
    /// green by winning a race is not evidence.</para>
    /// </summary>
    [Fact]
    public async Task Reconciler_DoesNotSweepAtStartup_ThenSweepsAfterOneInterval()
    {
        var h = new ScheduleHarness(enabled: true, chatFlag: true);
        await h.SeedDivergentAsync();

        await h.Reconciler.StartAsync(CancellationToken.None);
        try
        {
            // Real time passes; the reconciler's clock does not. Nothing may happen.
            await Task.Delay(400);
            h.Chat.Settles.Should().BeEmpty("no sweep before the first interval elapses");

            for (var i = 0; i < 40 && h.Chat.Settles.IsEmpty; i++)
            {
                h.Clock.Advance(h.Interval);
                await Task.Delay(50);
            }

            h.Chat.Settles.Should().NotBeEmpty("the sweep runs once the interval elapses");
        }
        finally
        {
            await h.Reconciler.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// G4.6 / G4.7 — the two off-switches. <c>Enabled=false</c> is the operator lever (stop
    /// sweeping while chat-service is drained); the Chat upstream flag off means there is
    /// no chat-service to settle against at all. Either one must stop the sweep completely,
    /// not merely reduce it — same harness and same waiting candidate as G4.5, which is
    /// what makes an empty result here mean "switched off" rather than "nothing to do".
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Reconciler_WhenSwitchedOff_NeverSweeps(bool enabled, bool chatFlag)
    {
        var h = new ScheduleHarness(enabled, chatFlag);
        await h.SeedDivergentAsync();

        await h.Reconciler.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 20; i++)
            {
                h.Clock.Advance(h.Interval);
                await Task.Delay(20);
            }

            h.Chat.Settles.Should().BeEmpty();
            h.Chat.Creates.Should().BeEmpty();
        }
        finally
        {
            await h.Reconciler.StopAsync(CancellationToken.None);
        }
    }

    // =====================================================================
    // telemetry — the signal that replaces the swallowed warning
    // =====================================================================

    /// <summary>
    /// G4.8 — a successful settle increments <c>chat.accept_settle.settled</c> exactly
    /// once, and moves no other counter. <c>settled</c> is the DENOMINATOR: a zero on
    /// <c>failures</c> is meaningless without it, because zero failures and zero accepts
    /// look identical.
    /// </summary>
    [Fact]
    public async Task Settle_Success_CountsExactlyOneSettled_AndNothingElse()
    {
        var h = new TelemetryHarness();
        var request = await h.SeedAssignedAsync();

        using var probe = new CounterProbe();
        await h.Settler.SettleAsync(request, Winner, CancellationToken.None);

        probe["chat.accept_settle.settled"].Should().Be(1);
        probe["chat.accept_settle.failures"].Should().Be(0);
        probe["chat.accept_settle.reconciled"].Should().Be(0);
    }

    /// <summary>
    /// G4.9 — THE UNRESOLVED PATH IS COUNTED. chat-service answers 200 to the create but
    /// hands back a conversation with no id, so nothing can be settled onto it.
    ///
    /// <para>This path returns rather than throwing, so NEITHER caller's catch fires: the
    /// accept controller's catch does not run and the reconciler's per-row catch does not
    /// run. If the counter were not incremented here, a winner locked out of the thread
    /// would leave every single counter reading clean — the precise shape of the defect
    /// GW5 exists to remove, reintroduced one layer down.</para>
    /// </summary>
    [Fact]
    public async Task Settle_WhenNoConversationCanBeResolved_IsUnresolved_AndCounted()
    {
        var h = new TelemetryHarness();
        h.Chat.CreateReturnsBlankId = true;
        var request = await h.SeedAssignedAsync();

        using var probe = new CounterProbe();
        var result = await h.Settler.SettleAsync(request, Winner, CancellationToken.None);

        result.Status.Should().Be(AcceptChatSettleStatus.Unresolved);
        probe["chat.accept_settle.failures"].Should().Be(1, "silence here is how the original defect hid");
        probe["chat.accept_settle.settled"].Should().Be(0);
    }

    /// <summary>
    /// G4.10 — a heal moves the two reconcile counters, and they are DISTINCT: divergent is
    /// what the sweep found, reconciled is what it actually repaired. Collapsing them would
    /// hide the case that matters most — a candidate found divergent every sweep and never
    /// repaired.
    /// </summary>
    [Fact]
    public async Task Sweep_Heal_CountsDivergentAndReconciled()
    {
        var h = new TelemetryHarness();
        await h.SeedAssignedAsync();

        using var probe = new CounterProbe();
        var healed = await h.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(1);
        probe["chat.accept_settle.reconcile_divergent"].Should().Be(1);
        probe["chat.accept_settle.reconciled"].Should().Be(1);
        probe["chat.accept_settle.settled"].Should().Be(1, "the heal goes through the same settler");
        probe["chat.accept_settle.failures"].Should().Be(0);
    }

    /// <summary>
    /// G4.11 — the complement: a clean, already-settled 1:1 moves NOTHING. Without this,
    /// G4.10 is satisfied by a reconciler that counts a divergence on every row it looks
    /// at.
    /// </summary>
    [Fact]
    public async Task Sweep_OnASettledConversation_MovesNoCounter()
    {
        var h = new TelemetryHarness();
        var request = await h.SeedAssignedAsync();
        await h.Settler.SettleAsync(request, Winner, CancellationToken.None);

        using var probe = new CounterProbe();
        var healed = await h.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(0);
        probe["chat.accept_settle.reconcile_divergent"].Should().Be(0);
        probe["chat.accept_settle.reconciled"].Should().Be(0);
        probe["chat.accept_settle.settled"].Should().Be(0);
    }

    /// <summary>
    /// G4.12 — a chat-service fault during a sweep is COUNTED as a failure and does not
    /// count as a heal. The row stays a candidate for the next sweep; the counter is the
    /// only thing that tells an operator a winner is still locked out.
    /// </summary>
    [Fact]
    public async Task Sweep_WhenChatFaults_CountsAFailure_AndHealsNothing()
    {
        var h = new TelemetryHarness();
        await h.SeedAssignedAsync();
        h.Chat.FailSettle = true;

        using var probe = new CounterProbe();
        var healed = await h.Reconciler.SweepOnceAsync(CancellationToken.None);

        healed.Should().Be(0);
        probe["chat.accept_settle.reconcile_divergent"].Should().Be(1, "it WAS found divergent");
        probe["chat.accept_settle.reconciled"].Should().Be(0, "…and it was NOT repaired");
        probe["chat.accept_settle.failures"].Should().Be(1);
    }

    // =====================================================================
    // helpers
    // =====================================================================

    private static WebApplicationFactory<Program> NewFactory()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Chat", "true" },
                    })));

    /// <summary>
    /// Reads the four GW5 counters off the meter for the duration of one action. Deltas,
    /// not totals — the counters are process-static, so a total would be whatever every
    /// earlier test in this collection left behind.
    ///
    /// <para>The collection attribute on this class is what makes the delta a measurement:
    /// the counters carry no tags, so a listener cannot attribute an increment to a caller,
    /// and a class running in parallel could silently supply the increment this test is
    /// looking for.</para>
    /// </summary>
    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _totals = new(StringComparer.Ordinal);

        public CounterProbe()
        {
            RuntimeHelpers.RunClassConstructor(typeof(ChatSettleTelemetry).TypeHandle);
            _listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == BusinessOutcomeTelemetry.MeterName
                    && inst.Name.StartsWith("chat.accept_settle.", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(inst);
                }
            };
            _listener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
                _totals.AddOrUpdate(inst.Name, measurement, (_, prev) => prev + measurement));
            _listener.Start();
        }

        public long this[string name] => _totals.TryGetValue(name, out var v) ? v : 0;

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Settler + reconciler over an in-memory store and a roster-modelling chat
    /// double. Same shape as G2's harness, rebuilt here so a G4 failure names a G4 cause.</summary>
    private sealed class TelemetryHarness
    {
        public FakeTimeProvider Clock { get; }
        public IRequestsStore Requests { get; }
        public ProbeChat Chat { get; }
        public IAcceptChatSettler Settler { get; }
        public AcceptChatSettleReconciler Reconciler { get; }

        public TelemetryHarness()
        {
            Clock = new FakeTimeProvider(T0);
            Requests = new InMemoryRequestsStore(Clock);
            Chat = new ProbeChat();

            var flags = Options.Create(new UpstreamFeatureFlags { Chat = true });
            Settler = new AcceptChatSettler(Chat, Requests, flags, NullLogger<AcceptChatSettler>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton(Requests);
            services.AddSingleton<IJeebConversationClient>(Chat);
            services.AddSingleton(Settler);

            Reconciler = new AcceptChatSettleReconciler(
                services.BuildServiceProvider(), Clock,
                Options.Create(new AcceptChatSettleReconcilerOptions
                {
                    LookBack = TimeSpan.FromHours(24),
                    PageSize = 50,
                }),
                flags, NullLogger<AcceptChatSettleReconciler>.Instance);
        }

        public Task<DeliveryRequest> SeedAssignedAsync() => SeedAssignedAsync(Requests, Clock);

        public static async Task<DeliveryRequest> SeedAssignedAsync(IRequestsStore store, FakeTimeProvider _)
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
            await store.SetJeeberIdAsync(created.Id, Winner, CancellationToken.None);
            return (await store.GetAsync(created.Id, CancellationToken.None))!;
        }
    }

    /// <summary>The reconciler under a running host clock. Same doubles, thread-safe
    /// collections, because the sweep runs on the thread pool here.</summary>
    private sealed class ScheduleHarness
    {
        public FakeTimeProvider Clock { get; }
        public TimeSpan Interval { get; } = TimeSpan.FromMinutes(2);
        public IRequestsStore Requests { get; }
        public ProbeChat Chat { get; }
        public AcceptChatSettleReconciler Reconciler { get; }

        public ScheduleHarness(bool enabled, bool chatFlag)
        {
            Clock = new FakeTimeProvider(T0);
            Requests = new InMemoryRequestsStore(Clock);
            Chat = new ProbeChat();

            var flags = Options.Create(new UpstreamFeatureFlags { Chat = chatFlag });
            var settler = new AcceptChatSettler(Chat, Requests, flags, NullLogger<AcceptChatSettler>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton(Requests);
            services.AddSingleton<IJeebConversationClient>(Chat);
            services.AddSingleton<IAcceptChatSettler>(settler);

            Reconciler = new AcceptChatSettleReconciler(
                services.BuildServiceProvider(), Clock,
                Options.Create(new AcceptChatSettleReconcilerOptions
                {
                    Enabled = enabled,
                    SweepInterval = Interval,
                    LookBack = TimeSpan.FromHours(24),
                    PageSize = 50,
                }),
                flags, NullLogger<AcceptChatSettleReconciler>.Instance);
        }

        public Task<DeliveryRequest> SeedDivergentAsync()
            => TelemetryHarness.SeedAssignedAsync(Requests, Clock);
    }

    /// <summary>
    /// chat-service double. Starts with nothing, so a candidate reads as 404 = divergent;
    /// a settle installs the settled 1:1 so a replay is observed converging rather than
    /// asserted to.
    /// </summary>
    private sealed class ProbeChat : IJeebConversationClient
    {
        private readonly ConcurrentDictionary<string, Convo> _byCorrelation = new(StringComparer.Ordinal);
        private int _seq;

        public bool CreateReturnsBlankId { get; set; }
        public bool FailSettle { get; set; }
        public ConcurrentQueue<CreateJeebConversationRequest> Creates { get; } = new();
        public ConcurrentQueue<SettleJeebConversationRequest> Settles { get; } = new();

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(string correlationKey, CancellationToken ct)
            => _byCorrelation.TryGetValue(correlationKey, out var c)
                ? Task.FromResult(c.ToResponse())
                : throw new JeebConversationApiException(HttpStatusCode.NotFound, null);

        public Task<JeebConversationResponse> CreateConversationAsync(CreateJeebConversationRequest request, CancellationToken ct)
        {
            Creates.Enqueue(request);
            if (CreateReturnsBlankId)
            {
                // A 200 that carries no usable id: nothing can be settled onto it.
                return Task.FromResult(new JeebConversationResponse { ConversationId = "" });
            }

            var convo = _byCorrelation.GetOrAdd(request.RequestId, key =>
            {
                var c = new Convo($"conv-{Interlocked.Increment(ref _seq)}", key);
                c.Participants[request.ClientUserId] = "client";
                return c;
            });
            return Task.FromResult(convo.ToResponse());
        }

        public Task<JeebConversationSettleResponse> SettleAsync(
            string conversationId, SettleJeebConversationRequest request, CancellationToken ct)
        {
            if (FailSettle)
                throw new JeebConversationApiException(HttpStatusCode.ServiceUnavailable, "chat-service unavailable");

            var convo = _byCorrelation.Values.FirstOrDefault(c => c.ConversationId == conversationId)
                ?? throw new JeebConversationApiException(HttpStatusCode.NotFound, null);

            Settles.Enqueue(request);
            lock (convo)
            {
                convo.Phase = request.Phase;
                convo.Participants[request.WinnerUserId] = request.WinnerRoleInConvo;
                if (request.RemoveOthers)
                {
                    foreach (var key in convo.Participants.Keys.ToList())
                    {
                        if (key != request.WinnerUserId && convo.Participants[key] != "client")
                        {
                            convo.Participants.Remove(key);
                        }
                    }
                }
            }

            return Task.FromResult(new JeebConversationSettleResponse
            {
                Conversation = convo.ToResponse(),
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
            public Dictionary<string, string> Participants { get; } = new(StringComparer.Ordinal);

            public JeebConversationResponse ToResponse()
            {
                lock (this)
                {
                    return new JeebConversationResponse
                    {
                        ConversationId = ConversationId,
                        CorrelationKey = CorrelationKey,
                        Phase = Phase,
                        Participants = Participants
                            .Select(kv => new JeebConversationParticipant { UserId = kv.Key, RoleInConvo = kv.Value })
                            .ToList(),
                    };
                }
            }
        }
    }
}
