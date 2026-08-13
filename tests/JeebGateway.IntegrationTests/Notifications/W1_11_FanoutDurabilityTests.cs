using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// gwdbx W1-11 — fan-out durability. Two guarantees are under test:
///   • the channel drain PERSISTS the job as a state-service work item BEFORE it fans out
///     (ordering, not merely "both happened"), so a crash mid-drain cannot lose the job; and
///   • a pre-send status probe DROPS a late fan-out whose request already left the
///     offer-wait window, so an expired/accepted request is never pushed.
/// The <c>FeatureFlags:UseUpstream:FanoutWorkQueue</c> key is created here and ships OFF;
/// the flip is W1-12.
/// </summary>
public class W1_11_FanoutDurabilityTests
{
    private const string RequestId = "req-w1-11";

    // =====================================================================
    // Ordering — persisted BEFORE fanned out.
    // =====================================================================

    [Fact]
    public async Task Drain_persists_the_work_item_before_it_fans_out()
    {
        var trace = new ConcurrentQueue<string>();
        var services = Provider(trace);
        var queue = new NewRequestFanoutQueue(8);
        var processor = new NewRequestFanoutProcessor(
            services, queue, NullLogger<NewRequestFanoutProcessor>.Instance);

        queue.TryEnqueue(Job()).Should().BeTrue();
        (await processor.DrainOnceAsync(CancellationToken.None)).Should().Be(1);

        trace.Should().Equal(
            new[] { "persist:" + RequestId, "fanout:" + RequestId },
            "the durable work item must exist before any push leaves the process");
    }

    [Fact]
    public async Task A_failed_persist_still_fans_out()
    {
        var trace = new ConcurrentQueue<string>();
        var services = Provider(trace, persistThrows: true);
        var queue = new NewRequestFanoutQueue(8);
        var processor = new NewRequestFanoutProcessor(
            services, queue, NullLogger<NewRequestFanoutProcessor>.Instance);

        queue.TryEnqueue(Job()).Should().BeTrue();
        await processor.DrainOnceAsync(CancellationToken.None);

        trace.Should().Equal(
            new[] { "persist:" + RequestId, "fanout:" + RequestId },
            "degrade-don't-fail: an unreachable work-item rail must never cost a live push");
    }

    [Fact]
    public void The_work_item_carries_the_named_idempotency_key_and_kind()
    {
        StateServiceNewRequestFanoutWorkItems.Kind.Should().Be("new-request-fanout");
        StateServiceNewRequestFanoutWorkItems.Application.Should().Be("jeeb-gateway");
        StateServiceNewRequestFanoutWorkItems.IdempotencyKeyFor(RequestId)
            .Should().Be("new-request-fanout:" + RequestId, "G-15 names the key explicitly");
    }

    [Fact]
    public void RetainPayloadTtl_defaults_to_the_longest_tier_auction_window()
        => new NewRequestFanoutOptions().RetainPayloadTtl
            .Should().Be(TierExpiryWindowResolver.SafeExpiryWindow);

    // =====================================================================
    // Pre-send status probe — a late fan-out never pushes a dead request.
    // =====================================================================

    [Theory]
    [InlineData(RequestStatus.Expired)]
    [InlineData(RequestStatus.Cancelled)]
    [InlineData(RequestStatus.Accepted)]
    [InlineData(RequestStatus.Delivered)]
    public async Task A_request_that_left_the_offer_wait_window_is_not_pushed(string status)
    {
        var push = new RecordingPushClient();
        var log = new CapturingLogger<NewRequestPushNotifier>();

        await Notifier(push, new StubStatusProbe(status), log)
            .FanOutAsync(Job(), CancellationToken.None);

        push.UserSends.Should().BeEmpty("the auction is over — a late fan-out must not push");
        push.TopicSends.Should().BeEmpty();
        push.Attempts.Should().Be(0);
        log.HasAny("stale-drop").Should().BeTrue("the drop must be visible in journalctl");
        log.HasAny("status=" + status).Should().BeTrue();
    }

    [Theory]
    [InlineData(RequestStatus.Pending)]
    [InlineData(RequestStatus.Matched)]
    [InlineData(RequestStatus.Scheduled)]
    public async Task A_request_still_in_the_offer_window_is_pushed(string status)
    {
        var push = new RecordingPushClient();

        await Notifier(push, new StubStatusProbe(status))
            .FanOutAsync(Job(), CancellationToken.None);

        push.RecipientIds.Should().Equal(new[] { "jeeberA" });
    }

    [Fact]
    public async Task An_unresolvable_status_fails_OPEN_and_still_pushes()
    {
        var push = new RecordingPushClient();

        await Notifier(push, AlwaysOpenFanoutStatusProbe.Instance)
            .FanOutAsync(Job(), CancellationToken.None);

        push.RecipientIds.Should().Equal(
            new[] { "jeeberA" }, "an unknown row or a store blip must never drop a push");
    }

    [Fact]
    public async Task The_probe_reads_the_real_requests_store()
    {
        var store = new InMemoryRequestsStore(TimeProvider.System);
        var row = await store.CreateAsync(
            new CreateRequestInput { ClientId = "customer-1", Description = "Pick up a package" },
            CancellationToken.None);
        var probe = new RequestsStoreFanoutStatusProbe(
            store, NullLogger<RequestsStoreFanoutStatusProbe>.Instance);

        (await probe.ReadStatusAsync(row.Id, CancellationToken.None))
            .Should().Be(RequestStatus.Pending);
        (await probe.ReadStatusAsync("no-such-request", CancellationToken.None))
            .Should().BeNull("an unknown row is unresolvable, and unresolvable fails OPEN");

        (await store.SetStatusAsync(row.Id, RequestStatus.Expired, CancellationToken.None))
            .Should().BeTrue();
        (await probe.ReadStatusAsync(row.Id, CancellationToken.None))
            .Should().Be(RequestStatus.Expired);
    }

    // =====================================================================
    // The W1-12 flag is created here and ships OFF.
    // =====================================================================

    [Fact]
    public void FanoutWorkQueue_flag_defaults_OFF()
        => new UpstreamFeatureFlags().FanoutWorkQueue.Should().BeFalse(
            "W1-11 only CREATES the key; the flip is W1-12");

    [Fact]
    public void No_appsettings_file_ships_the_FanoutWorkQueue_flag_on()
    {
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(FindRepoRoot(), "src", "JeebGateway"), "appsettings*.json")
            .Where(path => File.ReadAllText(path).Contains("FanoutWorkQueue"))
            .ToArray();

        offenders.Should().BeEmpty("a default deploy must behave exactly as main does today");
    }

    [Fact]
    public void The_topic_blast_kill_switch_keeps_its_own_semantics()
        => new NewRequestFanoutOptions().Enabled.Should().BeTrue(
            "Notifications:NewRequestFanout:Enabled is untouched by W1-11 — false still "
            + "restores the byte-identical legacy topic blast");

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static NewRequestNotification Job()
        => new(RequestId, "urgent", "Pick up a package", "customer-1",
               P1Fanout.DefaultLat, P1Fanout.DefaultLng);

    private static NewRequestPushNotifier Notifier(
        RecordingPushClient push,
        INewRequestFanoutStatusProbe probe,
        CapturingLogger<NewRequestPushNotifier>? log = null)
        => new(
            push,
            new JeebGateway.Tiers.TierCatalogResolver(new JeebGateway.Tiers.InMemoryTiersStore()),
            (Microsoft.Extensions.Logging.ILogger<NewRequestPushNotifier>?)log
                ?? NullLogger<NewRequestPushNotifier>.Instance,
            new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("jeeberA") } },
            new FakeUsersStore(),
            new RecordingFanoutQueue(),
            Options.Create(new NewRequestFanoutOptions()),
            TimeProvider.System,
            NullGenericEventDispatcher.Instance,
            probe);

    private static IServiceProvider Provider(
        ConcurrentQueue<string> trace, bool persistThrows = false)
    {
        var services = new ServiceCollection();
        services.AddScoped<INewRequestFanoutWorkItems>(
            _ => new TracingWorkItems(trace, persistThrows));
        services.AddScoped<INewRequestPushNotifier>(_ => new TracingNotifier(trace));
        return services.BuildServiceProvider();
    }

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JeebGateway")))
            dir = dir.Parent!;

        (dir is not null).Should().BeTrue("could not locate repo root from test source file path");
        return dir!.FullName;
    }

    private sealed class TracingWorkItems : INewRequestFanoutWorkItems
    {
        private readonly ConcurrentQueue<string> _trace;
        private readonly bool _throws;

        public TracingWorkItems(ConcurrentQueue<string> trace, bool throws)
        {
            _trace = trace;
            _throws = throws;
        }

        public Task<bool> PersistAsync(NewRequestNotification job, CancellationToken ct)
        {
            _trace.Enqueue("persist:" + job.RequestId);
            // The production adapter swallows its own faults; false is the "nothing durable" answer.
            return Task.FromResult(!_throws);
        }
    }

    private sealed class TracingNotifier : INewRequestPushNotifier
    {
        private readonly ConcurrentQueue<string> _trace;

        public TracingNotifier(ConcurrentQueue<string> trace) => _trace = trace;

        public Task NotifyNewRequestAsync(NewRequestNotification n, CancellationToken ct)
            => Task.CompletedTask;

        public Task FanOutAsync(NewRequestNotification n, CancellationToken ct)
        {
            _trace.Enqueue("fanout:" + n.RequestId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubStatusProbe : INewRequestFanoutStatusProbe
    {
        private readonly string? _status;

        public StubStatusProbe(string? status) => _status = status;

        public Task<string?> ReadStatusAsync(string requestId, CancellationToken ct)
            => Task.FromResult(_status);
    }
}
