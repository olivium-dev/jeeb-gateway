using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw3Pack;

/// <summary>
/// GW3 / W3.5(a) — RUNTIME half of "delete-not-wire the offer notifier".
///
/// The static half (a symbol grep at HEAD with a positive control at origin/main)
/// lives in <c>tests/gw3-pack/run-pack.sh</c>. A grep is not enough on its own for
/// two reasons, and each is an assertion below:
///
///   * a grep for a deleted NAME cannot see the same seam re-added under a
///     different name. A5/A6 census the live DI container and the live
///     controller signature instead of the source text.
///   * a delete assertion is trivially satisfied by deleting too much. The claim
///     GW3 makes is that the customer IS still notified, by the push
///     microservice. A7 drives the REAL submit pipeline and asserts that push
///     actually happens — it goes red if the wrong notifier was removed, while
///     every "the realtime seam is gone" assertion stays green.
///
/// NOT PROVEN HERE, deliberately. These are `suite` evidence (GATE.md §3). That a
/// push ever leaves the host, reaches the push microservice at :10040, or lights a
/// handset shade is `service` / `device` evidence and no assertion in this file
/// touches it. The push client is replaced by an in-process recorder.
/// </summary>
public class W35a_OfferRealtimeNotifierDeletedTests
{
    // ---------------------------------------------------------------------
    // A5 — DI census. Name-independent: it reads the container, not the source.
    // ---------------------------------------------------------------------
    [Fact]
    public void A5_NoInProcessOfferRealtimeSeamIsRegisteredInTheContainer()
    {
        var descriptors = CaptureRegistrations();

        // POSITIVE CONTROL FIRST. An empty census would satisfy the assertion
        // below for the wrong reason, and "zero hits" from an instrument that
        // never looked is the single most common false pass in this programme.
        descriptors.Should().NotBeEmpty("the census must have actually read the container");
        descriptors.Select(d => d.ServiceType.Name)
            .Should().Contain(nameof(IOfferPushNotifier),
                "POS control: the census CAN see an offer notifier — so the zero below means something");

        var realtimeSeams = descriptors
            .SelectMany(d => new[] { d.ServiceType, d.ImplementationType })
            .Where(t => t is not null && t.Assembly == typeof(Program).Assembly)
            .Select(t => t!.FullName ?? t.Name)
            .Where(n => n.Contains("Realtime", StringComparison.Ordinal)
                        && n.Contains("Notifier", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        realtimeSeams.Should().BeEmpty(
            "GW3 deleted the in-process offer realtime notifier rather than rewiring it; "
            + "a registration matching *Realtime*Notifier* in the gateway assembly means it came back");
    }

    // ---------------------------------------------------------------------
    // A6 — the LIVE controller's signature. This is the seam that mattered:
    // mobile really does POST /requests/{id}/offers, so the deleted notifier was
    // injected into a production code path, not a dev-only one.
    // ---------------------------------------------------------------------
    [Fact]
    public void A6_RequestOffersController_TakesNoInProcessRealtimeNotifier()
    {
        var ctors = typeof(RequestOffersController).GetConstructors();
        ctors.Should().ContainSingle("the controller has one injection point to audit");

        var parameterTypes = ctors[0].GetParameters().Select(p => p.ParameterType).ToList();

        // POS control: the ctor list is really being read, and the REAL notifier
        // is still on it.
        parameterTypes.Should().Contain(typeof(IOfferPushNotifier),
            "POS control: the customer's real notification seam is still injected here");

        parameterTypes
            .Where(t => t.Assembly == typeof(Program).Assembly)
            .Select(t => t.Name)
            .Where(n => n.Contains("Realtime", StringComparison.Ordinal))
            .Should().BeEmpty("the in-process realtime notifier is deleted, not re-wired");
    }

    // ---------------------------------------------------------------------
    // A7 — ANTI-OVER-DELETION, behavioural. Drives the real pipeline.
    // ---------------------------------------------------------------------
    [Fact]
    public async Task A7_Submit_StillNotifiesTheCustomer_ThroughThePushNotifier()
    {
        var push = new RecordingUserPushClient();
        using var factory = NewFactory(push);
        var detached = factory.Services
            .GetRequiredService<Fakes.AwaitableDetachedPushDispatcher>();

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeber = factory.CreateClient();
        jeeber.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        jeeber.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 11m, etaMinutes = 25, note = "GW3 A7" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "deleting the dead realtime seam must not change the submit contract");

        // The seat runs BEHIND the response by design, so wait on the fan-out itself.
        // Reading Sends straight after the 201 raced Task.Run and went red on CI (empty).
        await detached.DispatchedWork;

        push.Sends.Should().ContainSingle(
            "the customer's ONE real notification per accepted submission is the push dispatch; "
            + "if W3.5(a) had removed IOfferPushNotifier instead of the dead recorder, this is 0");
        push.Sends.Single().UserId.Should().Be(clientId,
            "the request's customer is notified, not the offering jeeber");
    }

    // ---------------------------------------------------------------------
    // A8 — assembly census of the new-offer notification contract. A rename can
    // dodge A5's *Realtime* substring; it cannot dodge "how many types in the
    // gateway declare a new-offer notification method".
    // ---------------------------------------------------------------------
    [Fact]
    public void A8_ExactlyTheTwoPushTypesDeclareANewOfferNotification()
    {
        var declaring = typeof(Program).Assembly
            .GetTypes()
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                     | BindingFlags.DeclaredOnly)
                         .Any(m => m.Name == "NotifyNewOfferAsync"))
            .Select(t => t.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        declaring.Should().BeEquivalentTo(
            new[] { nameof(IOfferPushNotifier), nameof(OfferPushNotifier) },
            "after GW3 the only new-offer notification in the gateway is the push-microservice "
            + "one; the deleted in-process recorder declared a third NotifyNewOfferAsync");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    /// <summary>Boots the real host and keeps a handle on the service collection as it
    /// stood after every registration (ConfigureTestServices runs last).</summary>
    private static IReadOnlyList<ServiceDescriptor> CaptureRegistrations()
    {
        List<ServiceDescriptor>? captured = null;
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => captured = s.ToList()));
        _ = factory.Services; // force the host to build
        return captured ?? new List<ServiceDescriptor>();
    }

    private static WebApplicationFactory<Program> NewFactory(RecordingUserPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServicePushNotificationClient>();
                services.AddSingleton<ServicePushNotificationClient>(push);
                // A7 asserts on a seat the gateway deliberately does not await; see the double.
                Fakes.AwaitableDetachedPushDispatcher.Use(services);
                // GW3 / W3.5(c): the gateway ships no in-memory offer store, so a test
                // that submits an offer supplies the ledger itself.
                Fakes.FakeOfferStoreWebApplicationFactory.UseFakeOfferStore(services);
            }));

    private static async Task<(string ClientId, string RequestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "GW3 W3.5(a) probe",
            // D2: the offer range guard needs a resolvable tier + pickup point.
            TierId = Fakes.InRangeGeoFixture.TierId,
            PickupLocation = new GeoPoint
            {
                Lat = Fakes.InRangeGeoFixture.Lat,
                Lng = Fakes.InRangeGeoFixture.Lng,
            },
        }, default);
        return (clientId, created.Id);
    }

    private sealed record SendRecord(string UserId, object Payload);

    private sealed class RecordingUserPushClient : ServicePushNotificationClient
    {
        public RecordingUserPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SendRecord> Sends { get; } = new();

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Sends.Enqueue(new SendRecord(user_id, body.Payload));
            return Task.FromResult(new SentPayloadResponse
            {
                Message = "ok",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }
}
