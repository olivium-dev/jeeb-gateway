using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Tracking;

/// <summary>
/// Standing gate on the architecture ruling: <b>the backend WRITES, the client
/// SUBSCRIBES.</b> Nothing in this gateway may open a Firestore listener, and
/// nothing in it may loop asking a datastore whether anything is new.
///
/// <para>Two defects are pinned here, one behavioural and one structural.</para>
///
/// <para><b>1. The fake stream.</b> <c>GET /deliveries/{id}/tracking</c> answered
/// <c>Accept: text/event-stream</c> by holding the connection open and running
/// <c>while (!ct.IsCancellationRequested) { await Task.Delay(SseInterval); …
/// _store.GetLatestAsync(…) }</c> at <c>Tracking:SseInterval</c> = 5 s. A stream
/// is supposed to be pushed to; this one was the gateway polling its OWN store
/// once per connected client, forever. <c>/v1/geo/jeeb/stream/{id}</c> existed
/// only to open it. Both are deleted.</para>
///
/// <para><b>2. The listener that must never appear.</b> Chat state reaches the
/// device because chat-service WRITES it to Firestore
/// (<c>FirestoreConversationStore.AddMessageAsync</c> →
/// <c>Conversations/{id}/Messages/{guid}</c>) and the device subscribes. If this
/// gateway ever grows a snapshot listener, the propagation direction has been
/// inverted and the ruling broken. It cannot even do so accidentally without
/// first taking a Firestore dependency — which is exactly what
/// <see cref="Gateway_Assembly_References_No_Firestore_Client_Library"/> forbids.</para>
///
/// <para><b>POSITIVE CONTROLS.</b> Every absence assertion in this file is paired
/// with a control, because a scanner that finds nothing is indistinguishable from
/// a scanner that is broken (I-11), and a structural check confirms shape while
/// only a mutation confirms power (I-14 / G23.1). The controls here are the
/// mutation, run in-process: the same matcher is pointed at a synthetic source
/// fragment that DOES contain the banned construct, and must fire. A control
/// that goes green-by-absence fails first and loudly.</para>
/// </summary>
public class NoBackendPollOrFirestoreListenerGuardTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Constructs that mean "this process is subscribing to Firestore". Any one of
    /// them in the shipped gateway inverts the write-only direction.
    /// </summary>
    private static readonly string[] FirestoreListenerMarkers =
    {
        "Google.Cloud.Firestore",
        "FirestoreDb",
        "FirestoreChangeListener",
        "OnSnapshot",
        "firestore.googleapis.com",
    };

    /// <summary>
    /// Constructs that mean "this process is faking a stream by re-reading on a
    /// timer". These are the literals the deleted SSE relay compiled into the
    /// assembly.
    /// </summary>
    private static readonly string[] ServerSidePollMarkers =
    {
        "text/event-stream",
        "SseInterval",
        "v1/geo/jeeb/stream",
    };

    /// <summary>
    /// A string literal that IS in the shipped assembly. The CLR stores literals
    /// UTF-16 (ECMA-335 #US heap), so this is the UTF-16 arm of the control.
    /// </summary>
    private const string Utf16ControlString = "https://jeeb.dev/errors/tracking-not-a-party";

    /// <summary>
    /// A live route template. Attribute arguments and metadata names live in the
    /// UTF-8 heaps, NOT #US — verified by this control failing when it was first
    /// written against UTF-16 only. This is the UTF-8 arm.
    /// </summary>
    private const string Utf8ControlString = "deliveries/{deliveryId}/tracking";

    private readonly WebApplicationFactory<Program> _factory;

    public NoBackendPollOrFirestoreListenerGuardTests(WebApplicationFactory<Program> factory)
        => _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Same swap the sibling tracking suites use: the GPS ingest path
                // heartbeats to delivery-service, and the in-process fake resolves
                // it without a live Go upstream.
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
            });
        });

    // ---------------- behavioural: the fake stream is gone ----------------------

    [Fact]
    public async Task Tracking_Route_Does_Not_Open_An_Event_Stream_Even_When_Asked_For_One()
    {
        // THE regression case. Before this change an explicit
        // Accept: text/event-stream selected StreamSseAsync, which answered
        // `Content-Type: text/event-stream` and then held the socket open,
        // re-reading ILocationStore every 5 seconds. This assertion fails on that
        // code: the media type is text/event-stream, not application/json.
        var seed = await SeedAsync();
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.70, Lng = 46.70, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        var http = AuthClient(seed.ClientId);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/deliveries/{seed.Id}/tracking");
        req.Headers.Add("Accept", "text/event-stream");

        // A 10 s budget the surviving one-shot read cannot possibly need. If the
        // poll loop is ever restored, this request never completes and the test
        // fails by timeout rather than hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var resp = await http.SendAsync(req, cts.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "an Accept: text/event-stream must NOT reopen the deleted 5s server-side re-read loop");

        var body = await resp.Content.ReadFromJsonAsync<TrackingPolylineDto>(JsonOptions, cts.Token);
        body!.DeliveryId.Should().Be(seed.Id);
        body.Position.Should().NotBeNull();
        body.Etag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sse_Alias_Route_Is_Gone()
    {
        // /v1/geo/jeeb/stream/{id} was a pure alias onto the poll loop. A party to
        // the delivery — who WOULD have been authorized — must now get 404, i.e.
        // the route is absent rather than merely forbidden.
        var seed = await SeedAsync();
        var http = AuthClient(seed.ClientId);

        var resp = await http.GetAsync($"/v1/geo/jeeb/stream/{seed.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the SSE alias existed only to open the server-side poll and is deleted with it");
    }

    [Fact]
    public async Task Participant_Gate_Survives_On_The_Snapshot_Route()
    {
        // RETAINED behaviour, asserted so the deletion cannot take the gate with
        // it: a non-party still gets 403 with an RFC 7807 body (N1).
        var seed = await SeedAsync();
        var http = AuthClient($"stranger-{Guid.NewGuid()}");

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/tracking");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    // ---------------- structural: nothing subscribes, nothing loops --------------

    [Fact]
    public void Scanner_Fires_On_A_Synthetic_Positive_Control()
    {
        // MUTATION CONTROL, both marker sets. A fragment that DOES contain each
        // banned construct is fed to the exact matcher used below. If this ever
        // goes quiet, every absence assertion in this file is worthless and this
        // test — not those — is the one that fails.
        const string firestoreListenerFragment = """
            using Google.Cloud.Firestore;
            var db = FirestoreDb.Create(projectId);
            var listener = db.Collection("Conversations").Listen(snapshot => { });
            """;
        MarkersFoundIn(firestoreListenerFragment, FirestoreListenerMarkers)
            .Should().Contain("Google.Cloud.Firestore").And.Contain("FirestoreDb",
                "the listener matcher must detect a listener when one is present");

        const string pollLoopFragment = """
            Response.Headers["Content-Type"] = "text/event-stream";
            var interval = _options.CurrentValue.SseInterval;
            while (!ct.IsCancellationRequested) { await Task.Delay(interval, ct); }
            """;
        MarkersFoundIn(pollLoopFragment, ServerSidePollMarkers)
            .Should().Contain("text/event-stream").And.Contain("SseInterval",
                "the poll matcher must detect a server-side re-read loop when one is present");
    }

    [Fact]
    public void Scanner_Finds_Strings_That_Are_Still_In_The_Shipped_Assembly()
    {
        // SECOND CONTROL, against the real artifact rather than a fragment: proves
        // the byte scan is reading a populated assembly and can match — in BOTH
        // encodings, because the absence assertions below claim both. Without it,
        // "no Firestore markers" is indistinguishable from "scanned an empty file".
        var bytes = GatewayAssemblyBytes();
        bytes.Length.Should().BeGreaterThan(100_000,
            "the shipped gateway assembly must be non-trivial for a byte scan to mean anything");

        AssemblyContains(bytes, Utf16ControlString, Encoding.Unicode).Should().BeTrue(
            $"'{Utf16ControlString}' is a live string literal; if the UTF-16 scan cannot find it, "
            + "the UTF-16 absence assertions below prove nothing");

        AssemblyContains(bytes, Utf8ControlString, Encoding.UTF8).Should().BeTrue(
            $"'{Utf8ControlString}' is a live route template; if the UTF-8 scan cannot find it, "
            + "the UTF-8 absence assertions below prove nothing");
    }

    [Fact]
    public void Gateway_Assembly_References_No_Firestore_Client_Library()
    {
        // Structural, and the strongest form available: you cannot open a snapshot
        // listener without the client library, so assert the reference itself is
        // absent from the gateway's compile-time closure.
        var referenced = typeof(JeebGateway.Tracking.ILocationStore).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        referenced.Should().NotBeEmpty("POSITIVE CONTROL — the reference list must be readable");
        referenced.Should().Contain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            "POSITIVE CONTROL — a reference this assembly certainly has must show up in the list");

        referenced.Should().NotContain(
            n => n.Contains("Firestore", StringComparison.OrdinalIgnoreCase)
              || n.Contains("FirebaseAdmin", StringComparison.OrdinalIgnoreCase),
            "the gateway writes through chat-service and never subscribes; taking a Firestore "
            + "client dependency here is the first step of inverting that");
    }

    [Fact]
    public void Shipped_Gateway_Assembly_Contains_No_Firestore_Listener_Marker()
    {
        var bytes = GatewayAssemblyBytes();
        foreach (var marker in FirestoreListenerMarkers)
        {
            // UTF-16 is how the CLR stores string literals (ECMA-335 #US heap) —
            // an ASCII-only grep of a DLL has produced a false absence on this
            // project before. Both encodings are checked.
            AssemblyContains(bytes, marker, Encoding.Unicode).Should().BeFalse(
                $"'{marker}' must not be a live string in the gateway (UTF-16)");
            AssemblyContains(bytes, marker, Encoding.UTF8).Should().BeFalse(
                $"'{marker}' must not appear as UTF-8 bytes in the gateway either");
        }
    }

    [Fact]
    public void Shipped_Gateway_Assembly_Contains_No_Server_Side_Poll_Marker()
    {
        var bytes = GatewayAssemblyBytes();
        foreach (var marker in ServerSidePollMarkers)
        {
            AssemblyContains(bytes, marker, Encoding.Unicode).Should().BeFalse(
                $"'{marker}' belonged to the deleted 5s server-side re-read loop (UTF-16)");
            AssemblyContains(bytes, marker, Encoding.UTF8).Should().BeFalse(
                $"'{marker}' belonged to the deleted 5s server-side re-read loop (UTF-8)");
        }
    }

    // -------------------------------- helpers -----------------------------------

    private static IReadOnlyList<string> MarkersFoundIn(string haystack, IEnumerable<string> markers)
        => markers.Where(m => haystack.Contains(m, StringComparison.Ordinal)).ToArray();

    private static byte[] GatewayAssemblyBytes()
    {
        var path = typeof(JeebGateway.Tracking.ILocationStore).Assembly.Location;
        File.Exists(path).Should().BeTrue(
            "the built gateway assembly must be resolvable for this scan to mean anything");
        return File.ReadAllBytes(path);
    }

    private static bool AssemblyContains(byte[] haystack, string needle, Encoding encoding)
        => IndexOf(haystack, encoding.GetBytes(needle)) >= 0;

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private HttpClient AuthClient(string userId)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        // ADR-005 §7 dual-role-one-token edge caller (delivery.track.own {client}
        // + delivery.gps.stream {jeeber}).
        c.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return c;
    }

    private async Task<Seed> SeedAsync()
    {
        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            DropoffLocation = new GeoPoint { Lat = 24.80, Lng = 46.80 }
        }, default);
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();
        await store.SetStatusAsync(created.Id, RequestStatus.HeadingOff, default);
        return new Seed(created.Id, clientId, jeeberId);
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId);
}
