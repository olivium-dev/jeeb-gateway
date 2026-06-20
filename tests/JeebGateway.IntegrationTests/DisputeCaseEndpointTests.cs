using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Disputes.V2;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Roles = JeebGateway.Users.Roles;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-BE-028 / JEB-64 — dispute case state machine + chat/GPS evidence
/// orchestration. Every AC from the Jira story is pinned to a test:
///
/// <list type="bullet">
///   <item>AC1: <see cref="Escalate_Attaches_Gps_Polyline_And_Empty_Chat_Transcript"/></item>
///   <item>AC2: <see cref="Resolve_With_Refund_Triggers_Payment_Gateway_And_Notifies_Both_Parties"/></item>
///   <item>AC3: <see cref="Resolve_When_Already_Resolved_Returns_409_Already_Resolved"/></item>
///   <item>AC4: <see cref="Resolve_As_Non_Admin_Returns_403"/> +
///     <see cref="ListMine_Includes_Cases_Where_User_Is_Counterparty"/></item>
///   <item>AC5: <see cref="Open_And_Resolve_Emit_Telemetry_Spans"/></item>
///   <item>AC6: <see cref="Escalate_Completes_Under_One_Second"/></item>
/// </list>
/// </summary>
public class DisputeCaseEndpointTests
{
    // ----------------------------------------------------------------
    // AC1 — escalate captures the GPS polyline as evidence. (Chat
    // transcript capture was REMOVED with the gateway chat BFF client: the
    // salehly mirror replaced jeeb's 1:1 chat aggregation with a passthrough
    // ChatController over the generic chat-service. Transcript evidence is now
    // empty/non-degraded until chat-service exposes a generic
    // transcript-by-participants read the gateway can call directly.)
    // ----------------------------------------------------------------
    [Fact]
    public async Task Escalate_Attaches_Gps_Polyline_And_Empty_Chat_Transcript()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac1";
        const string jeeber = "j-ac1";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber,
            pickup: new GeoPoint { Lat = 33.8938, Lng = 35.5018 },
            dropoff: new GeoPoint { Lat = 33.8869, Lng = 35.5131 });

        SeedJeeberLocation(factory, jeeber, lat: 33.8900, lng: 35.5100);

        var http = ClientFor(factory, client);
        var resp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods",
            Comment = "box arrived crushed",
            Photos = new List<string> { "https://cdn.example.com/p1.jpg" }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        body.Should().NotBeNull();
        body!.Id.Should().StartWith("case_");
        body.DeliveryId.Should().Be(deliveryId);
        body.OpenedByUserId.Should().Be(client);
        body.CounterpartyUserId.Should().Be(jeeber);
        body.State.Should().Be(DisputeCaseState.Open);
        body.Reason.Should().Be("damaged_goods");
        body.PhotoUrls.Should().ContainSingle();

        // Evidence: chat transcript is empty (capture removed) and the bundle
        // is not degraded — an absent transcript is the documented expected state.
        body.Evidence.Degraded.Should().BeFalse();
        body.Evidence.ChatTranscriptMessageCount.Should().Be(0);
        body.Evidence.ChatTranscriptJson.Should().BeNullOrEmpty();

        // Evidence: GPS polyline contains pickup → jeeber-fix → dropoff.
        body.Evidence.GpsPolyline.Should().HaveCount(3);
        body.Evidence.GpsPolyline[0].Should().BeEquivalentTo(new[] { 33.8938, 35.5018 });
        body.Evidence.GpsPolyline[1].Should().BeEquivalentTo(new[] { 33.8900, 35.5100 });
        body.Evidence.GpsPolyline[2].Should().BeEquivalentTo(new[] { 33.8869, 35.5131 });
    }

    // ----------------------------------------------------------------
    // AC2 — admin resolves with refund → unified_payment_gateway records
    // a refund AND both parties get notifications.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Resolve_With_Refund_Triggers_Payment_Gateway_And_Notifies_Both_Parties()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac2";
        const string jeeber = "j-ac2";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ac2");
        var resolveResp = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 12.50m,
            Notes = "approved"
        });

        resolveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await resolveResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        resolved!.State.Should().Be(DisputeCaseState.ResolvedRefund);
        resolved.RefundUsd.Should().Be(12.50m);
        resolved.RefundLedgerEntryId.Should().NotBeNullOrEmpty();
        resolved.ResolverAdminId.Should().Be("admin-ac2");

        // unified_payment_gateway recorded the refund with the case id
        // as the idempotency key (RECORD-ID lookups against the
        // InMemoryPaymentRefundClient stand in for the upstream check).
        var refundClient = factory.Services.GetRequiredService<InMemoryPaymentRefundClient>();
        refundClient.Entries.Should().ContainSingle(r => r.CaseId == @case.Id
            && r.AmountUsd == 12.50m
            && r.IdempotencyKey == $"dispute:{@case.Id}:refund");

        // Both parties received a DisputeUpdate push at resolve time.
        var tracker = factory.Services.GetRequiredService<InMemoryPushDeliveryTracker>();
        var openerPushes = await tracker.GetForUserAsync(client, CancellationToken.None);
        openerPushes.Should().Contain(p => p.Trigger == NotificationTrigger.DisputeUpdate,
            "filer must receive a DisputeUpdate push on resolution");

        var counterpartyPushes = await tracker.GetForUserAsync(jeeber, CancellationToken.None);
        counterpartyPushes.Should().Contain(p => p.Trigger == NotificationTrigger.DisputeUpdate,
            "counter-party must receive a DisputeUpdate push on resolution (AC2 dual fan-out)");
    }

    // ----------------------------------------------------------------
    // AC3 — second resolve on a terminal case returns 409 already_resolved.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Resolve_When_Already_Resolved_Returns_409_Already_Resolved()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac3";
        const string jeeber = "j-ac3";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "no_delivery"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ac3");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action",
            Notes = "insufficient evidence"
        })).EnsureSuccessStatusCode();

        var second = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 5m
        });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("already_resolved");
    }

    // ----------------------------------------------------------------
    // AC4 — only admin role can resolve.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Resolve_As_Non_Admin_Returns_403()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac4-na";
        const string jeeber = "j-ac4-na";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "no_delivery"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var resp = await http.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action",
            Notes = "user trying to resolve their own case"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------
    // AC4 — regular users only see their own cases (or cases they are
    // the counter-party on); a stranger gets 403 on a foreign case and
    // an empty list on their own /v1/disputes view.
    // ----------------------------------------------------------------
    [Fact]
    public async Task ListMine_Includes_Cases_Where_User_Is_Counterparty()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac4-list";
        const string jeeber = "j-ac4-list";
        const string stranger = "u-ac4-stranger";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var clientHttp = ClientFor(factory, client);
        var fileResp = await clientHttp.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        // Client (opener) sees their case.
        var clientList = await clientHttp.GetFromJsonAsync<DisputeCaseListResponse>("/v1/disputes");
        clientList!.Total.Should().Be(1);
        clientList.Items.Single().Id.Should().Be(@case!.Id);

        // Jeeber (counter-party) ALSO sees the case — AC4 "regular users
        // see their own cases" must include cases where they are the
        // delivery's counter-party.
        var jeeberHttp = ClientFor(factory, jeeber, role: Roles.Jeeber);
        var jeeberList = await jeeberHttp.GetFromJsonAsync<DisputeCaseListResponse>("/v1/disputes");
        jeeberList!.Total.Should().Be(1);
        jeeberList.Items.Single().Id.Should().Be(@case.Id);

        // Stranger sees nothing.
        var strangerHttp = ClientFor(factory, stranger);
        var strangerList = await strangerHttp.GetFromJsonAsync<DisputeCaseListResponse>("/v1/disputes");
        strangerList!.Total.Should().Be(0);

        // …and 403 on direct lookup.
        (await strangerHttp.GetAsync($"/v1/disputes/{@case.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------
    // AC5 — open + resolve emit structured spans on the v2 ActivitySource
    // and the open path stamps both the case id and the elapsed time.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Open_And_Resolve_Emit_Telemetry_Spans()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac5";
        const string jeeber = "j-ac5";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == DisputeCaseTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                lock (spans) spans.Add(a);
            }
        };
        ActivitySource.AddActivityListener(listener);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "safety_concern",
            Comment = "driver was unsafe"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ac5");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action",
            Notes = "closed"
        })).EnsureSuccessStatusCode();

        lock (spans)
        {
            spans.Should().Contain(a => a.OperationName == "dispute.case.open"
                && a.Tags.Any(t => t.Key == "case.id" && t.Value == @case.Id));
            spans.Should().Contain(a => a.OperationName == "dispute.case.resolve"
                && a.Tags.Any(t => t.Key == "case.id" && t.Value == @case.Id));
        }
    }

    // ----------------------------------------------------------------
    // AC6 — escalate (evidence capture) completes within the 1 second open
    // budget. Chat transcript capture is removed, so the bundle is just the
    // GPS polyline; the latency budget still applies to the escalate path.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Escalate_Completes_Under_One_Second()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ac6";
        const string jeeber = "j-ac6";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var stopwatch = Stopwatch.StartNew();
        var resp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        stopwatch.Stop();

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "AC6 — escalate evidence capture must complete within the 1 second budget");

        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        body!.Evidence.ChatTranscriptMessageCount.Should().Be(0);
        body.Evidence.Degraded.Should().BeFalse();
    }

    // ----------------------------------------------------------------
    // Idempotency — replaying /escalate with the same Idempotency-Key
    // returns the original case (PO blocker #6).
    // ----------------------------------------------------------------
    [Fact]
    public async Task Escalate_Replay_With_Same_Idempotency_Key_Returns_Existing_Case()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-idem";
        const string jeeber = "j-idem";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);
        var http = ClientFor(factory, client);
        http.DefaultRequestHeaders.Add("Idempotency-Key", "user-supplied-key-1");

        var first = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var second = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        secondBody!.Id.Should().Be(firstBody!.Id, "same idempotency key must return the existing case");
    }

    // ----------------------------------------------------------------
    // Idempotency — replaying /resolve with the same Idempotency-Key
    // does NOT double-refund and returns the existing terminal row.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Resolve_Replay_With_Same_Idempotency_Key_Does_Not_Double_Refund()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-res-idem";
        const string jeeber = "j-res-idem";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);
        var http = ClientFor(factory, client);

        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-res-idem");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", "resolve-key-1");

        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 7m
        })).EnsureSuccessStatusCode();

        // Replay with the same key — must return 200 and NOT post a second
        // refund (the refund client also de-dupes on its own key, but we
        // additionally short-circuit before calling the refund path).
        var replay = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 7m
        });
        replay.StatusCode.Should().Be(HttpStatusCode.OK);

        var refundClient = factory.Services.GetRequiredService<InMemoryPaymentRefundClient>();
        refundClient.Entries.Count(e => e.CaseId == @case.Id).Should().Be(1,
            "idempotency-key replay must not double-refund");
    }

    [Fact]
    public async Task Escalate_Without_Reason_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-vr", "j-vr");
        var http = ClientFor(factory, "c-vr");

        var resp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "   "
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Escalate_Against_Unknown_Delivery_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = ClientFor(factory, "c-404");

        var resp = await http.PostAsJsonAsync("/v1/deliveries/does-not-exist/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Escalate_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-anon", "j-anon");
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Escalate_Twice_For_Same_Delivery_Returns_409()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-dup", "j-dup");
        var http = ClientFor(factory, "c-dup");

        (await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        })).EnsureSuccessStatusCode();

        var second = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------------
    // DIS-02 — the full status machine open → under_review → resolved is
    // reachable through the API. Before this WS the under_review state was
    // dead (no endpoint moved a case into it). An admin triages the case,
    // both parties can read the under_review status, then resolution lands.
    // ----------------------------------------------------------------
    [Fact]
    public async Task MarkUnderReview_Moves_Open_To_UnderReview_Then_Resolvable()
    {
        using var factory = new WebApplicationFactory<Program>();
        const string client = "c-ur";
        const string jeeber = "j-ur";

        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        @case!.State.Should().Be(DisputeCaseState.Open);

        var admin = AdminClientFor(factory, "admin-ur");
        var reviewResp = await admin.PostAsync($"/admin/v1/disputes/{@case.Id}/review", null);
        reviewResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewed = await reviewResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        reviewed!.State.Should().Be(DisputeCaseState.UnderReview);

        // The customer sees under_review on their timeline (DIS-02).
        var fromClient = await http.GetFromJsonAsync<DisputeCaseResponse>($"/v1/disputes/{@case.Id}");
        fromClient!.State.Should().Be(DisputeCaseState.UnderReview);

        // under_review → resolved_refund is a valid onward transition.
        var resolveResp = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action",
            Notes = "reviewed and closed"
        });
        resolveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await resolveResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        resolved!.State.Should().Be(DisputeCaseState.ResolvedNoAction);
    }

    [Fact]
    public async Task MarkUnderReview_Is_Idempotent_NoOp_On_Second_Call()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-ur-idem", "j-ur-idem");

        var http = ClientFor(factory, "c-ur-idem");
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ur-idem");
        (await admin.PostAsync($"/admin/v1/disputes/{@case!.Id}/review", null)).EnsureSuccessStatusCode();

        // Second pickup (e.g. two admins on the queue) is a no-op 200, not a 409.
        var second = await admin.PostAsync($"/admin/v1/disputes/{@case.Id}/review", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await second.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        body!.State.Should().Be(DisputeCaseState.UnderReview);
    }

    [Fact]
    public async Task MarkUnderReview_On_Resolved_Case_Returns_409_Already_Resolved()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-ur-res", "j-ur-res");

        var http = ClientFor(factory, "c-ur-res");
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ur-res");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action"
        })).EnsureSuccessStatusCode();

        // Cannot re-open a terminal case to under_review.
        var review = await admin.PostAsync($"/admin/v1/disputes/{@case.Id}/review", null);
        review.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await review.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("already_resolved");
    }

    [Fact]
    public async Task MarkUnderReview_As_Non_Admin_Returns_403()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-ur-na", "j-ur-na");

        var http = ClientFor(factory, "c-ur-na");
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        // A regular user cannot triage their own case.
        var resp = await http.PostAsync($"/admin/v1/disputes/{@case!.Id}/review", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkUnderReview_On_Unknown_Case_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var admin = AdminClientFor(factory, "admin-ur-404");

        var resp = await admin.PostAsync("/admin/v1/disputes/case_does_not_exist/review", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_Refund_Without_Amount_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "c-amt", "j-amt");
        var http = ClientFor(factory, "c-amt");

        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-amt");
        var resp = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role = Roles.Client)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static HttpClient AdminClientFor(WebApplicationFactory<Program> factory, string adminId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", adminId);
        c.DefaultRequestHeaders.Add("X-User-Roles", Roles.Admin);
        return c;
    }

    private static async Task<string> SeedDeliveryWithJeeberAsync(
        WebApplicationFactory<Program> factory,
        string clientId,
        string jeeberId,
        GeoPoint? pickup = null,
        GeoPoint? dropoff = null)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "test delivery",
            PickupLocation = pickup,
            DropoffLocation = dropoff
        }, CancellationToken.None);

        // Bind the Jeeber directly so the counter-party is set on the
        // dispute case at escalate time. Going through the full accept
        // path is out of scope here — the store's TryAcceptByJeeberAsync
        // checks BR-10 caps + pre-acceptance status, which we don't need
        // for these tests.
        var lookup = await store.GetAsync(created.Id, CancellationToken.None);
        if (lookup is not null)
        {
            lookup.JeeberId = jeeberId;
        }
        return created.Id;
    }

    private static void SeedJeeberLocation(WebApplicationFactory<Program> factory, string jeeberId, double lat, double lng)
    {
        var store = factory.Services.GetRequiredService<ILocationStore>();
        store.Record(jeeberId, new[]
        {
            new GpsPointDto
            {
                Lat = lat,
                Lng = lng,
                Accuracy = 5.0,
                Timestamp = DateTimeOffset.UtcNow
            }
        });
    }
}

/// <summary>
/// DIS-02 — pins the dispute-case transition matrix as a pure unit. The
/// <c>under_review</c> column was dead before this WS (no endpoint reached
/// it); these theories guard the documented machine
/// <c>open → under_review → resolved_*</c> + the terminal seals.
/// </summary>
public class DisputeCaseStateMachineTests
{
    [Theory]
    // open is the only legal source for under_review
    [InlineData(DisputeCaseState.Open, DisputeCaseState.UnderReview, true)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.UnderReview, false)]   // no self-loop
    [InlineData(DisputeCaseState.ResolvedRefund, DisputeCaseState.UnderReview, false)] // terminal → review blocked
    [InlineData(DisputeCaseState.ResolvedNoAction, DisputeCaseState.UnderReview, false)]
    [InlineData(DisputeCaseState.Closed, DisputeCaseState.UnderReview, false)]
    // resolve is legal from both open and under_review
    [InlineData(DisputeCaseState.Open, DisputeCaseState.ResolvedRefund, true)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.ResolvedRefund, true)]
    [InlineData(DisputeCaseState.Open, DisputeCaseState.ResolvedNoAction, true)]
    [InlineData(DisputeCaseState.UnderReview, DisputeCaseState.ResolvedNoAction, true)]
    // resolved is terminal — no onward resolve
    [InlineData(DisputeCaseState.ResolvedRefund, DisputeCaseState.ResolvedNoAction, false)]
    [InlineData(DisputeCaseState.ResolvedNoAction, DisputeCaseState.ResolvedRefund, false)]
    public void CanTransition_Matches_Documented_Machine(string from, string to, bool expected)
    {
        DisputeCaseState.CanTransition(from, to).Should().Be(expected);
    }

    [Theory]
    [InlineData(DisputeCaseState.Open, false)]
    [InlineData(DisputeCaseState.UnderReview, false)]
    [InlineData(DisputeCaseState.ResolvedRefund, true)]
    [InlineData(DisputeCaseState.ResolvedNoAction, true)]
    [InlineData(DisputeCaseState.Closed, true)]
    public void IsResolved_Classifies_Terminal_States(string state, bool expected)
    {
        DisputeCaseState.IsResolved(state).Should().Be(expected);
    }
}
