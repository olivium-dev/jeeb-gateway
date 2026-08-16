using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// PHASE-V D4 — the <c>newreq-fanout</c> summary line is the acceptance evidence for OA-21,
/// so it may never claim more delivery than the gateway can prove.
///
/// <para>OBSERVED LIVE 2026-08-16 13:16:13 on gateway main 939935c (MSI): one real client
/// request resolved 6 recipients and logged
/// <c>candidates=6 recipients=6 … sent=6 failed=0 fcmAcceptedRows=0 fcmRejectedRows=0</c>.
/// Exactly ONE of the six reached a device: push-service answered 201 for one recipient and
/// 404 ("Push notification records for user … not found") for the other five. The line said
/// six were sent and none failed, and the one counter that could have contradicted it
/// (<c>fcmAcceptedRows=0</c>) sat right next to it, unused.</para>
///
/// <para>These tests pin the accounting, NOT the audience. <c>candidates=</c>/<c>recipients=</c>
/// are load-bearing OA-21 evidence and are asserted here only to prove they survive.</para>
/// </summary>
public class FanoutSendAccountingTests
{
    private const string RequestId = "req-accounting";
    private const string TierId = "urgent";

    // T1 — the live scenario, verbatim: every recipient goes to notification-service, which
    // accepts the EVENT. The gateway learns nothing about devices, so it must not say "sent".
    [Fact]
    public async Task Handover_Fanout_Does_Not_Report_HandedOver_Recipients_As_Sent()
    {
        var push = new StatusAwarePushClient();
        var store = new FakeAvailabilityStore { Online = Jeebers(6) };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var events = new CapturingGenericEventDispatcher();

        await Notifier(push, store, log, events)
            .FanOutAsync(Job(), CancellationToken.None);

        push.Attempts.Should().Be(0,
            "notification-service is the sole push producer; the gateway must not dial :10040 itself");
        events.Sent.Should().HaveCount(6);

        // The audience half must keep discriminating — this is OA-21's evidence.
        log.Has(LogLevel.Information, "candidates=6 recipients=6").Should().BeTrue();

        log.HasAny("sent=6").Should().BeFalse(
            "the gateway handed 6 events to notification-service and saw ZERO device outcomes; "
            + "'sent=6' is a delivery claim it cannot back — this is the Phase-V D4 lie");
        log.Has(LogLevel.Information, "handedOver=6").Should().BeTrue(
            "the honest count is 'handed to the producer', and it must be on the line");
        log.Has(LogLevel.Information, "deviceEvidence=notification-service").Should().BeTrue(
            "the line must state that device outcomes are NOT visible from here");
        log.Has(LogLevel.Information, "fcmAcceptedRows=n/a fcmRejectedRows=n/a").Should().BeTrue(
            "the gateway never called :10040 on this rail, so a numeric 0 row count would be "
            + "a THIRD false zero: unknown must not print as none");
    }

    // T2 — the direct rail: a 404 means "this user has no registered device". That is a
    // legitimate terminal outcome, neither a success nor a retryable failure.
    [Fact]
    public async Task DeviceLess_Recipients_Are_Not_Counted_As_Sent_Or_Failed()
    {
        var push = new StatusAwarePushClient
        {
            StatusForUser = id => id == "has-device" ? null : 404,
        };
        var store = new FakeAvailabilityStore
        {
            Online = new[]
            {
                P1Fanout.Jeeber("has-device"),
                P1Fanout.Jeeber("no-device-1"), P1Fanout.Jeeber("no-device-2"),
                P1Fanout.Jeeber("no-device-3"), P1Fanout.Jeeber("no-device-4"),
                P1Fanout.Jeeber("no-device-5"),
            },
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();

        await Notifier(push, store, log).FanOutAsync(Job(), CancellationToken.None);

        log.Has(LogLevel.Information, "recipients=6").Should().BeTrue();
        log.Has(LogLevel.Information, "sent=1").Should().BeTrue(
            "exactly one recipient had a device row the relay accepted");
        log.Has(LogLevel.Information, "noDevice=5").Should().BeTrue(
            "a 404 is 'this user has no registered device' — its own outcome, counted as such");
        log.Has(LogLevel.Information, "failed=0").Should().BeTrue(
            "a device-less recipient is not a failure: nothing broke and nothing is retryable");
        log.HasAny("sent=6").Should().BeFalse();
    }

    // T3 — retryable and terminal must not share a bucket: 404 terminal, 503 retryable.
    [Fact]
    public async Task Retryable_And_Terminal_Failures_Are_Reported_Separately()
    {
        var push = new StatusAwarePushClient
        {
            StatusForUser = id => id switch
            {
                "ok" => null,
                "gone-1" or "gone-2" or "gone-3" => 404,
                _ => 503,
            },
        };
        var store = new FakeAvailabilityStore
        {
            Online = new[]
            {
                P1Fanout.Jeeber("ok"),
                P1Fanout.Jeeber("gone-1"), P1Fanout.Jeeber("gone-2"), P1Fanout.Jeeber("gone-3"),
                P1Fanout.Jeeber("blip-1"), P1Fanout.Jeeber("blip-2"),
            },
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();

        await Notifier(push, store, log).FanOutAsync(Job(), CancellationToken.None);

        log.Has(LogLevel.Information, "sent=1").Should().BeTrue();
        log.Has(LogLevel.Information, "noDevice=3").Should().BeTrue(
            "three recipients are terminally device-less — retrying them is 3 wasted POSTs per attempt");
        log.Has(LogLevel.Information, "failed=2").Should().BeTrue(
            "only the two 503s are retryable failures");
        log.Has(LogLevel.Information, "deviceEvidence=gateway-direct").Should().BeTrue();
    }

    // T4 — every recipient lands in exactly one bucket, so the line can never double-count.
    [Fact]
    public async Task Outcome_Buckets_Partition_The_Recipient_Set()
    {
        var push = new StatusAwarePushClient
        {
            StatusForUser = id => id.StartsWith("gone", StringComparison.Ordinal) ? 404 : null,
        };
        var store = new FakeAvailabilityStore
        {
            Online = new[]
            {
                P1Fanout.Jeeber("ok-1"), P1Fanout.Jeeber("ok-2"),
                P1Fanout.Jeeber("gone-1"), P1Fanout.Jeeber("gone-2"),
            },
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();

        await Notifier(push, store, log).FanOutAsync(Job(), CancellationToken.None);

        log.Has(LogLevel.Information,
                "recipients=4 initiatorExcluded=0 roleFiltered=0 roleUnknown=0 "
                + "sent=2 handedOver=0 noDevice=2 failed=0 failedTerminal=0")
            .Should().BeTrue("2 + 0 + 2 + 0 + 0 = 4, the whole recipient set, exactly once each");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static NewRequestNotification Job()
        => new(RequestId, TierId, "Pick up a package", "customer-1",
               P1Fanout.DefaultLat, P1Fanout.DefaultLng);

    private static IReadOnlyList<JeeberAvailability> Jeebers(int n)
    {
        var rows = new List<JeeberAvailability>(n);
        for (var i = 0; i < n; i++)
        {
            rows.Add(P1Fanout.Jeeber("jeeber-" + i));
        }

        return rows;
    }

    private static NewRequestPushNotifier Notifier(
        ServicePushNotificationClient push,
        FakeAvailabilityStore availability,
        CapturingLogger<NewRequestPushNotifier> log,
        IGenericEventDispatcher? events = null)
        => new(
            push,
            new JeebGateway.Tiers.TierCatalogResolver(new JeebGateway.Tiers.InMemoryTiersStore()),
            log,
            availability,
            new FakeUsersStore(),
            new RecordingFanoutQueue(),
            Options.Create(new NewRequestFanoutOptions()),
            TimeProvider.System,
            events ?? NullGenericEventDispatcher.Instance,
            AlwaysOpenFanoutStatusProbe.Instance);
}

/// <summary>
/// Push double that answers with a REAL relay status. 404 is what :10040 returns for a user
/// with no registered device row; the NSwag client surfaces it as ApiException.
/// </summary>
internal sealed class StatusAwarePushClient : ServicePushNotificationClient
{
    public StatusAwarePushClient() : base("http://localhost", new HttpClient()) { }

    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public ConcurrentQueue<string> UserSends { get; } = new();

    /// <summary>Status for this recipient; null answers 201 carrying one accepted device row.</summary>
    public Func<string, int?>? StatusForUser { get; init; }

    public override Task<SentPayloadResponse> Send_notification_to_userAsync(
        string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);

        if (StatusForUser?.Invoke(user_id) is int status)
        {
            throw new ApiException(
                status == 404 ? "Not found" : "Upstream failure",
                status,
                status == 404
                    ? "{\"detail\":\"Push notification records for user " + user_id + " not found\"}"
                    : "{\"detail\":\"internal error\"}",
                new Dictionary<string, IEnumerable<string>>(),
                null!);
        }

        UserSends.Enqueue(user_id);
        return Task.FromResult(new SentPayloadResponse
        {
            Message = "Notification sent successfully to 1 device(s)",
            Timestamp = DateTimeOffset.UtcNow,
        });
    }
}
