using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Push;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Whisper;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P1 — the request-created → "finding jeebers" fan-out. A new-request push used to be ONE
/// blast to the <c>jeeb_jeebers</c> FCM topic, which reaches every subscriber INCLUDING the
/// customer who just created the request. P1 replaces that with a per-user fan-out over the
/// capability-gated <c>jeeber_availability</c> roster, with the initiator removed.
///
/// Two layers, mirroring <see cref="OfferPushNotifierTests"/>:
///   • unit tests on <see cref="NewRequestPushNotifier.FanOutAsync"/> against a recording
///     push client that captures BOTH the per-user rail and the legacy topic seam (so
///     "zero topic sends" is assertable), a settable <see cref="FakeAvailabilityStore"/>,
///     and a capturing logger (the <c>newreq-fanout</c> line IS the acceptance evidence); and
///   • END-TO-END wiring through the REAL create pipelines (JSON
///     <c>POST /v1/requests</c> and the multipart voice surface) with the fan-out queue
///     replaced by a recorder — proving what the hot path enqueues (request id, INITIATOR,
///     pickup point), that reject paths enqueue nothing, and that the 201 is no longer on
///     the push's critical path.
/// </summary>
public class NewRequestPushNotifierTests
{
    private const string RequestId = "req-99";

    // A tier id that EXISTS in the gateway's seeded in-process catalog
    // (JeebGateway.Tiers.InMemoryTiersStore) so the notifier resolves it to a human
    // display name for the body suffix. The raw id is still carried flat; the body
    // shows the NAME.
    private const string TierId = "urgent";
    private const string TierName = "Urgent";

    // Builds the notifier over the SAME seeded tier catalog the app serves at
    // GET /v1/tiers, so "urgent" → "Urgent" resolves exactly as it does in prod.
    private static NewRequestPushNotifier NewNotifier(
        RecordingPushClient push,
        FakeAvailabilityStore? availability = null,
        NewRequestFanoutOptions? options = null,
        INewRequestFanoutQueue? queue = null,
        ILogger<NewRequestPushNotifier>? logger = null,
        FakeUsersStore? users = null)
        => new(
            push,
            new JeebGateway.Tiers.TierCatalogResolver(new JeebGateway.Tiers.InMemoryTiersStore()),
            logger ?? NullLogger<NewRequestPushNotifier>.Instance,
            availability ?? new FakeAvailabilityStore(),
            users ?? new FakeUsersStore(),
            queue ?? new RecordingFanoutQueue(),
            Options.Create(options ?? new NewRequestFanoutOptions()),
            TimeProvider.System);

    private static NewRequestNotification Job(
        string? initiator = null,
        string? requestId = RequestId,
        string? tierId = TierId,
        string? description = "Pick up a package",
        double? lat = P1Fanout.DefaultLat,
        double? lng = P1Fanout.DefaultLng)
        // D2: a pickup point is now a PRECONDITION of any fan-out, so the default job carries
        // one. A job without it reaches nobody, which is the fail-closed behaviour under test.
        => new(requestId!, tierId, description, initiator, lat, lng);

    // =====================================================================
    // Unit — FanOutAsync against a recording per-user client.
    // =====================================================================

    [Fact] // G-U1 — the direct refutation of the observed defect.
    public async Task Initiator_Is_Never_A_Recipient()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = new[] { P1Fanout.Jeeber("jeeberA"), P1Fanout.Jeeber("jeeberB"), P1Fanout.Jeeber("nour") }
        };
        var notifier = NewNotifier(push, store);

        await notifier.FanOutAsync(Job(initiator: "nour"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "jeeberA", "jeeberB" },
            "the customer who created the request must never be pushed their own request");
        push.RecipientIds.Should().NotContain("nour");
    }

    [Fact] // G-U2
    public async Task Sends_PerUser_Never_Topic()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = new[] { P1Fanout.Jeeber("a"), P1Fanout.Jeeber("b"), P1Fanout.Jeeber("c") }
        };
        var notifier = NewNotifier(push, store);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.UserSends.Should().HaveCount(3);
        push.TopicSends.Should().BeEmpty(
            "a topic blast cannot express 'exclude the initiator' — regressing to it re-opens P1");
    }

    [Fact] // G-U3
    public async Task NonJeeber_Never_Receives()
    {
        // The audience source is the capability-gated jeeber_availability roster
        // (AvailabilityController is class-level [RequireCapability(AvailabilityToggle)] and is
        // the only writer on a user path), so a customer-only account is structurally
        // unreachable — it simply has no row.
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("jeeberA") } };
        var notifier = NewNotifier(push, store);

        await notifier.FanOutAsync(Job(initiator: "customer-only-1"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "jeeberA" });
    }

    [Fact] // G-U4 — the wire contract old APKs and the mobile deep-link depend on.
    public async Task PerUser_Payload_Is_Unchanged()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("jeeberA") } };
        var notifier = NewNotifier(push, store);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        var payload = (IDictionary<string, object?>)push.UserSends.Single().Payload;
        payload["title"].Should().Be("New delivery request");
        payload["type"].Should().Be("new_request");
        payload["category"].Should().Be("delivery");
        payload["priority"].Should().Be("high");
        payload["audience"].Should().Be("jeebers");
        payload["audience_role"].Should().Be(JeebGateway.Users.Roles.Jeeber);
        // Both id variants are carried flat so the mobile deep-link (routes /orders/:id from
        // delivery_id/order_id/requestId fallback) resolves regardless of which key it reads.
        payload["requestId"].Should().Be(RequestId);
        payload["request_id"].Should().Be(RequestId);
        // The RAW tier id is carried flat (machine field), unchanged by display resolution.
        payload["tierId"].Should().Be(TierId);
        // Routing fields are flat top-level entries — no nested "data" object.
        payload.Should().NotContainKey("data");
        ((string)payload["body"]!).Should().Contain("Pick up a package");
        ((string)payload["body"]!).Should().Contain($" • {TierName}");
    }

    [Fact] // G-U5 — under-notification must be LOUD, never silent.
    public async Task Empty_Recipient_Set_Is_A_Loud_NoOp()
    {
        var push = new RecordingPushClient();
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(push, new FakeAvailabilityStore(), logger: log);

        var act = async () => await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        push.UserSends.Should().BeEmpty();
        push.TopicSends.Should().BeEmpty();
        log.Has(LogLevel.Warning, "recipients=0").Should().BeTrue(
            "an empty recipient set is the R1 regression signal and must reach journalctl");
    }

    [Fact] // G-U6 — R8: a device-less recipient makes the relay 404; the batch must survive.
    public async Task One_Failing_Send_Does_Not_Abort_Fanout()
    {
        var push = new RecordingPushClient { ThrowForUser = id => id == "B" };
        var store = new FakeAvailabilityStore
        {
            Online = new[] { P1Fanout.Jeeber("A"), P1Fanout.Jeeber("B"), P1Fanout.Jeeber("C") }
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(push, store, logger: log);

        var act = async () => await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        push.RecipientIds.Should().BeEquivalentTo(new[] { "A", "C" });
        log.Has(LogLevel.Information, "sent=2 handedOver=0 noDevice=0 failed=1").Should().BeTrue(
            "the aggregate counts are the operator-facing signal; per-recipient faults stay at Debug");
    }

    [Fact] // G-U7 — the R1 mitigation.
    public async Task FallsBackToKnownJeebers_WhenNoneOnline()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = Array.Empty<JeeberAvailability>(),
            Known = new[] { P1Fanout.Jeeber("jeeberA"), P1Fanout.Jeeber("jeeberB") }
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(
            push, store, new NewRequestFanoutOptions { FallbackToKnownJeebers = true }, logger: log);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "jeeberA", "jeeberB" });
        log.Has(LogLevel.Information, "source=known").Should().BeTrue();
        store.LastKnownSince.Should().NotBeNull("the roster read is windowed, not unbounded");
    }

    [Fact] // G-U7b
    public async Task KnownFallback_Disabled_SendsNothing()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = Array.Empty<JeeberAvailability>(),
            Known = new[] { P1Fanout.Jeeber("jeeberA") }
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(
            push, store, new NewRequestFanoutOptions { FallbackToKnownJeebers = false }, logger: log);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.UserSends.Should().BeEmpty();
        push.TopicSends.Should().BeEmpty();
        log.Has(LogLevel.Warning, "recipients=0").Should().BeTrue();
    }

    [Fact] // G-U8
    public async Task Dedupes_And_Caps_Recipients()
    {
        var rows = new List<JeeberAvailability>
        {
            P1Fanout.Jeeber("USER-DUP"),
            P1Fanout.Jeeber("user-dup"),
        };
        for (var i = 0; i < 12; i++)
        {
            rows.Add(P1Fanout.Jeeber($"jeeber-{i:00}"));
        }

        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore { Online = rows };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(
            push, store, new NewRequestFanoutOptions { MaxRecipients = 10 }, logger: log);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.UserSends.Should().HaveCount(10, "MaxRecipients caps the blast radius");
        push.RecipientIds.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(10,
            "the same id in two casings is ONE recipient");
        log.Has(LogLevel.Warning, "recipients-truncated").Should().BeTrue(
            "an overflow is logged, never silently dropped");
    }

    [Fact] // G-U9 — staged geo filter (ships OFF; this proves the code that lands with it).
    public async Task GeoRadius_Filters_When_Configured()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = new[]
            {
                P1Fanout.Jeeber("near", 33.885, 35.505),
                P1Fanout.Jeeber("far", 34.5, 36.5),
                P1Fanout.Jeeber("noCoords", lat: null, lng: null),
            }
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(
            push, store, new NewRequestFanoutOptions { RadiusKm = 5 }, logger: log);

        await notifier.FanOutAsync(
            Job(initiator: "customer-1", lat: 33.88, lng: 35.50), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "near" },
            "D2: a row without stored coordinates cannot be proven in range, so it is EXCLUDED. "
            + "Keeping it was the fail-open that let a ~9,000 km request reach a nearby jeeber");
        log.Has(LogLevel.Information, "source=online+geo").Should().BeTrue();
    }

    [Fact] // G-U10 (D2 REVERSAL) — an emptied geo filter must NOT revert to the unfiltered set.
    public async Task GeoFilter_Emptying_Sends_To_Nobody()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = new[]
            {
                P1Fanout.Jeeber("farA", 34.5, 36.5),
                P1Fanout.Jeeber("farB", 35.5, 37.5),
            }
        };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(
            push, store, new NewRequestFanoutOptions { RadiusKm = 1 }, logger: log);

        await notifier.FanOutAsync(
            Job(initiator: "customer-1", lat: 33.88, lng: 35.50), CancellationToken.None);

        push.RecipientIds.Should().BeEmpty(
            "this test used to assert the OPPOSITE — keeping the unfiltered online set when the "
            + "radius emptied it. That fallback is bug D2: it pushed every out-of-range request "
            + "to everyone online. An empty in-range set is the correct answer");
        log.Has(LogLevel.Information, "geo-filter-emptied").Should().BeTrue();
    }

    [Fact] // G-U12
    public async Task Initiator_Match_Is_Format_And_Case_Insensitive()
    {
        // PostgresAvailabilityStore.MapRow emits Guid.ToString() (lowercase "D"), while the
        // initiator id comes from the JWT `sub` and may differ in case/format.
        var upperGuid = "A1B2C3D4-0000-0000-0000-000000000001";
        var guidPush = new RecordingPushClient();
        await NewNotifier(guidPush, new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber(upperGuid) } })
            .FanOutAsync(Job(initiator: upperGuid.ToLowerInvariant()), CancellationToken.None);

        guidPush.UserSends.Should().BeEmpty("the same GUID in a different casing is the same user");

        // Non-GUID ids fall back to a trimmed, case-insensitive string match.
        var opaquePush = new RecordingPushClient();
        await NewNotifier(opaquePush, new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("user-x") } })
            .FanOutAsync(Job(initiator: "  USER-X  "), CancellationToken.None);

        opaquePush.UserSends.Should().BeEmpty();
    }

    [Fact] // G-U13
    public async Task BlankRequestId_EnqueuesNothing()
    {
        var queue = new RecordingFanoutQueue();
        var notifier = NewNotifier(new RecordingPushClient(), queue: queue);

        var act = async () => await notifier.NotifyNewRequestAsync(
            Job(initiator: "customer-1", requestId: "  "), CancellationToken.None);

        await act.Should().NotThrowAsync();
        queue.Jobs.Should().BeEmpty();
    }

    [Fact] // G-U14 — degrade-don't-fail.
    public async Task PushServiceFault_IsSwallowed_NeverThrows()
    {
        var push = new RecordingPushClient { Throw = true };
        var store = new FakeAvailabilityStore { Online = new[] { P1Fanout.Jeeber("A") } };
        var notifier = NewNotifier(push, store);

        var act = async () => await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        push.Attempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact] // G-U15 — the hot path never blocks, and an overflow is never silent.
    public async Task QueueFull_DropsAndWarns()
    {
        var queue = new NewRequestFanoutQueue(capacity: 1);
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(new RecordingPushClient(), queue: queue, logger: log);

        await notifier.NotifyNewRequestAsync(Job(initiator: "c", requestId: "req-1"), CancellationToken.None);

        var act = async () => await notifier.NotifyNewRequestAsync(
            Job(initiator: "c", requestId: "req-2"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        queue.PendingCount.Should().Be(1, "the buffer is capacity-1 and was never drained");
        log.Has(LogLevel.Warning, "queue full").Should().BeTrue();
    }

    // ── RC-2 — send-time role re-validation, KNOWN fallback rung only ────────

    [Fact] // RC-2(a) — a dual-role account acting as customer is exactly the complaint population.
    public async Task KnownFallback_Drops_Candidate_Whose_ActiveRole_Is_Customer()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = Array.Empty<JeeberAvailability>(),
            Known = new[] { P1Fanout.Jeeber("acting-jeeber"), P1Fanout.Jeeber("acting-customer") }
        };
        var users = new FakeUsersStore()
            .WithActiveRole("acting-jeeber", JeebGateway.Users.Roles.Jeeber)
            .WithActiveRole("acting-customer", JeebGateway.Users.Roles.Client);
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(push, store, logger: log, users: users);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "acting-jeeber" },
            "an ever-was-a-jeeber roster row whose CURRENT ActiveRole is customer must not be pushed");
        log.Has(LogLevel.Information, "roleFiltered=1").Should().BeTrue(
            "the drop must be auditable on the newreq-fanout summary line");
    }

    [Fact] // RC-2(b) — opaque 'driver' AND contract 'jeeber' spellings both keep, case-insensitively.
    public async Task KnownFallback_Keeps_Driver_And_Jeeber_ActiveRole_Spellings()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Known = new[] { P1Fanout.Jeeber("opaque"), P1Fanout.Jeeber("contract"), P1Fanout.Jeeber("cased") }
        };
        var users = new FakeUsersStore()
            .WithActiveRole("opaque", "driver")
            .WithActiveRole("contract", "jeeber")
            .WithActiveRole("cased", "JEEBER");
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(push, store, logger: log, users: users);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "opaque", "contract", "cased" });
        log.Has(LogLevel.Information, "roleFiltered=0").Should().BeTrue();
    }

    [Fact] // RC-2(c) — no positive evidence, no drop: the filter may only ever shrink on proof.
    public async Task KnownFallback_Keeps_ProfileMissing_Candidate_And_Counts_RoleUnknown()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Known = new[] { P1Fanout.Jeeber("jeeberA"), P1Fanout.Jeeber("ghost-no-profile") }
        };
        var users = new FakeUsersStore().WithActiveRole("jeeberA", "driver");
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(push, store, logger: log, users: users);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "jeeberA", "ghost-no-profile" },
            "a candidate with no readable profile is KEPT — degrade-don't-fail, never under-notify");
        log.Has(LogLevel.Information, "roleUnknown=1").Should().BeTrue();
    }

    [Fact] // RC-2(d) — a users-store outage must never fail or empty the fan-out.
    public async Task KnownFallback_UsersStore_Fault_Keeps_Candidates_And_Fanout_Completes()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Known = new[] { P1Fanout.Jeeber("A"), P1Fanout.Jeeber("B") }
        };
        var users = new FakeUsersStore { Throw = true };
        var log = new CapturingLogger<NewRequestPushNotifier>();
        var notifier = NewNotifier(push, store, logger: log, users: users);

        var act = async () => await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        push.RecipientIds.Should().BeEquivalentTo(new[] { "A", "B" },
            "the role filter may only ever SHRINK the set on positive evidence, never on a fault");
        log.Has(LogLevel.Information, "roleUnknown=2").Should().BeTrue();
        log.Has(LogLevel.Information, "sent=2").Should().BeTrue("the fan-out itself must still complete");
    }

    [Fact] // RC-2(e) — an online row is a deliberate jeeber-mode act; the online rung stays unfiltered.
    public async Task Online_Rung_Is_Never_RoleFiltered()
    {
        var push = new RecordingPushClient();
        var store = new FakeAvailabilityStore
        {
            Online = new[] { P1Fanout.Jeeber("online-now-customer") }
        };
        var users = new FakeUsersStore().WithActiveRole("online-now-customer", "customer");
        var notifier = NewNotifier(push, store, users: users);

        await notifier.FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        push.RecipientIds.Should().BeEquivalentTo(new[] { "online-now-customer" });
        users.Lookups.Should().Be(0, "no per-candidate profile lookup may run on the online rung");
    }

    [Fact] // RC-2(h) — the fallback window default.
    public async Task KnownJeeberWindow_Defaults_To_7_Days()
    {
        new NewRequestFanoutOptions().KnownJeeberWindow.Should().Be(TimeSpan.FromDays(7));

        var store = new FakeAvailabilityStore { Known = new[] { P1Fanout.Jeeber("a") } };
        await NewNotifier(new RecordingPushClient(), store)
            .FanOutAsync(Job(initiator: "customer-1"), CancellationToken.None);

        store.LastKnownSince.Should().NotBeNull();
        store.LastKnownSince!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow - TimeSpan.FromDays(7), TimeSpan.FromMinutes(1));
    }

    // ── C-6 — options validated at startup: fail-to-start, never invert ──────

    [Fact] // C-6(g) — a non-positive cap would silently empty the recipient set.
    public void Gateway_Refuses_To_Start_When_MaxRecipients_Is_NonPositive()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Notifications:NewRequestFanout:MaxRecipients"] = "0",
                })));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*Notifications:NewRequestFanout:MaxRecipients*",
                "the failure must name the key an operator has to fix");
    }

    [Fact] // C-6(g)
    public void Gateway_Refuses_To_Start_When_KnownJeeberWindow_Is_NonPositive()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Notifications:NewRequestFanout:KnownJeeberWindow"] = "00:00:00",
                })));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*Notifications:NewRequestFanout:KnownJeeberWindow*");
    }

    [Fact] // C-6(g) — deploy-safe: MSI env sets only PerSendTimeout/TotalBudget; defaults must pass.
    public void Gateway_Boots_With_MsiEnvShape_And_Defaults_Pass_Validation()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Notifications:NewRequestFanout:PerSendTimeout"] = "00:00:10",
                    ["Notifications:NewRequestFanout:TotalBudget"] = "00:01:00",
                })));

        var boot = () => factory.CreateClient();

        boot.Should().NotThrow();
        var opts = factory.Services.GetRequiredService<IOptions<NewRequestFanoutOptions>>().Value;
        opts.MaxRecipients.Should().Be(500);
        opts.KnownJeeberWindow.Should().Be(TimeSpan.FromDays(7));
    }

    // =====================================================================
    // Wiring — the REAL create pipelines enqueue the right job.
    // =====================================================================

    [Fact] // G-W1
    public async Task JsonCreate_EnqueuesExactlyOneJob_WithInitiatorAndPickup()
    {
        var queue = new RecordingFanoutQueue();
        using var factory = NewFactory(queue: queue);
        var userId = $"client-{Guid.NewGuid()}";
        var client = ClientFor(factory, userId);

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Pick up groceries"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<CreatedRequestDto>())!;

        queue.Jobs.Should().ContainSingle("exactly one fan-out job per accepted create");
        var job = queue.Jobs.Single();
        job.RequestId.Should().Be(dto.Id);
        job.TierId.Should().Be(TierId);
        job.InitiatorUserId.Should().Be(userId,
            "the fan-out cannot exclude the initiator unless the create tells it who that is");
        job.PickupLat.Should().Be(33.88);
        job.PickupLng.Should().Be(35.50);
    }

    [Fact] // G-W2
    public async Task VoiceCreate_EnqueuesJob_WithInitiator()
    {
        var queue = new RecordingFanoutQueue();
        using var factory = NewFactory(queue: queue, voice: true);
        var userId = $"client-{Guid.NewGuid()}";
        var client = ClientFor(factory, userId);

        var resp = await client.PostAsync("/v1/requests", VoiceForm(Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        queue.Jobs.Should().ContainSingle();
        queue.Jobs.Single().InitiatorUserId.Should().Be(userId);
    }

    [Fact] // G-W3
    public async Task RejectedCreates_EnqueueNothing()
    {
        var queue = new RecordingFanoutQueue();
        using var factory = NewFactory(queue: queue);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("   "));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        queue.Jobs.Should().BeEmpty("a rejected (400) create never reaches the fan-out hook");
    }

    [Fact] // G-W4 — R2: the hot path is an in-memory TryWrite, not an HTTP round-trip.
    public async Task Create201_IsNotDelayedByFanout()
    {
        // REAL queue + REAL hosted processor, with a push client that stalls 3s per send.
        var push = new RecordingPushClient { Delay = TimeSpan.FromSeconds(3) };
        var availability = new FakeAvailabilityStore
        {
            Online = Enumerable.Range(0, 5).Select(i => P1Fanout.Jeeber($"jeeber-{i}")).ToArray()
        };
        using var factory = NewFactory(push: push, availability: availability);
        var client = ClientFor(factory, $"client-{Guid.NewGuid()}");

        // Warm the host so first-request JIT/startup is not measured.
        (await client.PostAsJsonAsync("/v1/requests", ValidPayload("warm-up")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsJsonAsync("/v1/requests", ValidPayload("Deliver documents"));
        sw.Stop();

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "the create hot path only enqueues; recipient resolution and the sends run off it");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        RecordingPushClient? push = null,
        FakeAvailabilityStore? availability = null,
        INewRequestFanoutQueue? queue = null,
        bool voice = false)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Replace the deployed :10040 push client with the recorder so no real
                    // network call happens and the emitted recipients/payload are asserted.
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push ?? new RecordingPushClient());

                    if (availability is not null)
                    {
                        services.RemoveAll<IAvailabilityStore>();
                        services.AddSingleton<IAvailabilityStore>(availability);
                    }

                    if (queue is not null)
                    {
                        // Deterministic: the recorder's reader never yields, so the real
                        // hosted processor idles instead of racing the assertions.
                        services.RemoveAll<INewRequestFanoutQueue>();
                        services.AddSingleton(queue);
                    }

                    if (voice)
                    {
                        services.AddSingleton<IVoiceTranscriptionClient>(new StubVoiceClient());
                        services.Configure<UpstreamFeatureFlags>(f => f.Voice = true);
                    }
                });
            });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    /// <summary>Minimum valid JSON create body — description + tier + WGS84 pickup/dropoff.</summary>
    private static object ValidPayload(string description) => new
    {
        description,
        tierId = TierId,
        pickupLocation = new { lat = 33.88, lng = 35.50 },
        dropoffLocation = new { lat = 33.89, lng = 35.51 },
    };

    private static MultipartFormDataContent VoiceForm(string requestId)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(new byte[] { 1, 2, 3 });
        part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(part, "audio", "ar-5s.wav");
        form.Add(new StringContent(requestId), "requestId");
        form.Add(new StringContent("standard"), "tier");
        return form;
    }

    private sealed record CreatedRequestDto(string Id, string ClientId, string Status, string Description);

    /// <summary>Deterministic upstream stub — returns a fixed transcript + confidence.</summary>
    private sealed class StubVoiceClient : IVoiceTranscriptionClient
    {
        public Task<TranscriptionResult> TranscribeAsync(WhisperAudio audio, string language, CancellationToken ct)
            => TranscribeVoiceAsync(audio, language, null, ct);

        public Task<TranscriptionResult> TranscribeVoiceAsync(
            WhisperAudio audio, string language, string? idempotencyKey, CancellationToken ct)
            => Task.FromResult(new TranscriptionResult(
                AudioId: Guid.NewGuid().ToString("n"),
                Outcome: TranscriptionOutcome.Transcribed,
                Transcription: new WhisperTranscription("كيلو بندورة من السوق", language, 0.93),
                Reason: null));
    }
}
