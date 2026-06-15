using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Disputes.V2;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Roles = JeebGateway.Users.Roles;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S14 / JEB-64 — admin dispute-queue + state-machine surface introduced in PR #193:
/// <c>GET /admin/v1/disputes</c> (+ <c>?state=</c> filter), <c>POST /admin/v1/disputes/{id}/review</c>
/// (open → under_review) and <c>POST /admin/v1/disputes/{id}/close</c> (resolved_* → closed),
/// plus the <c>NotAParty → 403</c> escalate gate.
///
/// These are the negative/positive integration cases the three independent reviews
/// flagged as missing (zero test files in the original diff). Every new public endpoint
/// gets a happy-path AND a negative-path case (org backend Definition of Done), and the
/// admin-only authz on the cross-user queue + the invalid-transition 409s are pinned so a
/// future capability-map or state-machine edit can't silently widen/break them.
/// </summary>
public class DisputeCaseAdminQueueEndpointTests
{
    // ----------------------------------------------------------------
    // Admin queue — admin sees the cross-user queue; non-admins are
    // rejected at L2 ([RequireCapability(dispute.read.queue)] = AdminOnly).
    // ----------------------------------------------------------------
    [Fact]
    public async Task AdminQueue_As_Admin_Returns_All_Cases()
    {
        using var factory = new WebApplicationFactory<Program>();

        // Two cases opened by two different clients.
        await OpenCaseAsync(factory, "c-q1", "j-q1", "damaged_goods");
        await OpenCaseAsync(factory, "c-q2", "j-q2", "overcharged");

        var admin = AdminClientFor(factory, "admin-queue");
        var resp = await admin.GetAsync("/admin/v1/disputes");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseListResponse>();
        body!.Total.Should().Be(2, "the admin queue is cross-user — it returns every case");
        body.Items.Should().OnlyContain(i => i.State == DisputeCaseState.Open);
    }

    [Theory]
    [InlineData(Roles.Client)]
    [InlineData(Roles.Jeeber)]
    public async Task AdminQueue_As_Non_Admin_Returns_403(string role)
    {
        using var factory = new WebApplicationFactory<Program>();
        await OpenCaseAsync(factory, "c-q-403", "j-q-403", "damaged_goods");

        var http = ClientFor(factory, "u-q-403", role);
        var resp = await http.GetAsync("/admin/v1/disputes");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the cross-user queue is the only thing standing between a {0} token and every user's disputes — it must be AdminOnly at L2", role);
    }

    [Fact]
    public async Task AdminQueue_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();

        var resp = await anon.GetAsync("/admin/v1/disputes");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminQueue_State_Filter_Narrows_To_That_State()
    {
        using var factory = new WebApplicationFactory<Program>();

        // One stays open; one is moved to under_review.
        await OpenCaseAsync(factory, "c-f-open", "j-f-open", "damaged_goods");
        var underReview = await OpenCaseAsync(factory, "c-f-ur", "j-f-ur", "overcharged");

        var admin = AdminClientFor(factory, "admin-filter");
        (await admin.PostAsync($"/admin/v1/disputes/{underReview}/review", null)).EnsureSuccessStatusCode();

        var openOnly = await admin.GetFromJsonAsync<DisputeCaseListResponse>("/admin/v1/disputes?state=open");
        openOnly!.Items.Should().OnlyContain(i => i.State == DisputeCaseState.Open);
        openOnly.Items.Should().ContainSingle();

        var urOnly = await admin.GetFromJsonAsync<DisputeCaseListResponse>("/admin/v1/disputes?state=under_review");
        urOnly!.Items.Should().ContainSingle(i => i.Id == underReview);
    }

    [Fact]
    public async Task AdminQueue_State_Filter_Accepts_Hyphen_Wire_Spelling()
    {
        using var factory = new WebApplicationFactory<Program>();

        // Resolve a case with refund so it lands in resolved_refund (wire: resolved-refund).
        var caseId = await OpenCaseAsync(factory, "c-hy", "j-hy", "overcharged");
        var admin = AdminClientFor(factory, "admin-hyphen");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{caseId}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 5m
        })).EnsureSuccessStatusCode();

        // The hyphenated wire spelling (resolved-refund) must filter the same row
        // the underscore token (resolved_refund) does — NormalizeState maps both.
        var hyphen = await admin.GetFromJsonAsync<DisputeCaseListResponse>("/admin/v1/disputes?state=resolved-refund");
        hyphen!.Items.Should().ContainSingle(i => i.Id == caseId);
        hyphen.Items.Single().State.Should().Be("resolved-refund", "the wire contract hyphenates resolved_* states");

        var underscore = await admin.GetFromJsonAsync<DisputeCaseListResponse>("/admin/v1/disputes?state=resolved_refund");
        underscore!.Items.Should().ContainSingle(i => i.Id == caseId);
    }

    // ----------------------------------------------------------------
    // Review (open → under_review) state machine.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Review_Open_Case_Transitions_To_Under_Review()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-rv", "j-rv", "damaged_goods");

        var admin = AdminClientFor(factory, "admin-rv");
        var resp = await admin.PostAsync($"/admin/v1/disputes/{caseId}/review", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        body!.State.Should().Be(DisputeCaseState.UnderReview);
        body.ReviewedByAdminId.Should().Be("admin-rv");
        body.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Review_Already_Under_Review_Returns_409_Invalid_Transition()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-rv2", "j-rv2", "damaged_goods");

        var admin = AdminClientFor(factory, "admin-rv2");
        (await admin.PostAsync($"/admin/v1/disputes/{caseId}/review", null)).EnsureSuccessStatusCode();

        // Second claim is no longer open → 409 invalid-transition (N6c).
        var second = await admin.PostAsync($"/admin/v1/disputes/{caseId}/review", null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("invalid-transition");
    }

    [Fact]
    public async Task Review_Resolved_Case_Returns_409_Invalid_Transition()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-rv3", "j-rv3", "overcharged");

        var admin = AdminClientFor(factory, "admin-rv3");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{caseId}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action"
        })).EnsureSuccessStatusCode();

        var resp = await admin.PostAsync($"/admin/v1/disputes/{caseId}/review", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Review_Unknown_Case_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var admin = AdminClientFor(factory, "admin-rv404");

        var resp = await admin.PostAsync("/admin/v1/disputes/case_does_not_exist/review", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Review_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-rv401", "j-rv401", "damaged_goods");
        var anon = factory.CreateClient();

        var resp = await anon.PostAsync($"/admin/v1/disputes/{caseId}/review", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Review_As_Non_Admin_Returns_403()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-rv-na", "j-rv-na", "damaged_goods");

        var http = ClientFor(factory, "u-rv-na", Roles.Client);
        var resp = await http.PostAsync($"/admin/v1/disputes/{caseId}/review", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------
    // Close (resolved_* → closed) terminal seal.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Close_Resolved_Case_Transitions_To_Closed()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-cl", "j-cl", "overcharged");

        var admin = AdminClientFor(factory, "admin-cl");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{caseId}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 9m
        })).EnsureSuccessStatusCode();

        var resp = await admin.PostAsync($"/admin/v1/disputes/{caseId}/close", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        body!.State.Should().Be(DisputeCaseState.Closed);
        body.ClosedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Close_Open_Case_Returns_409_Invalid_Transition()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-cl-open", "j-cl-open", "damaged_goods");

        var admin = AdminClientFor(factory, "admin-cl-open");
        // N6a: closing an open case is illegal.
        var resp = await admin.PostAsync($"/admin/v1/disputes/{caseId}/close", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("invalid-transition");
    }

    [Fact]
    public async Task Close_Under_Review_Case_Returns_409_Invalid_Transition()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-cl-ur", "j-cl-ur", "damaged_goods");

        var admin = AdminClientFor(factory, "admin-cl-ur");
        (await admin.PostAsync($"/admin/v1/disputes/{caseId}/review", null)).EnsureSuccessStatusCode();

        // under_review → closed is illegal (must resolve first).
        var resp = await admin.PostAsync($"/admin/v1/disputes/{caseId}/close", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Close_Already_Closed_Case_Returns_409_Invalid_Transition()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-cl-2x", "j-cl-2x", "overcharged");

        var admin = AdminClientFor(factory, "admin-cl-2x");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{caseId}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action"
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/admin/v1/disputes/{caseId}/close", null)).EnsureSuccessStatusCode();

        // closed is fully terminal — a second close is illegal.
        var second = await admin.PostAsync($"/admin/v1/disputes/{caseId}/close", null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Close_Unknown_Case_Returns_404()
    {
        using var factory = new WebApplicationFactory<Program>();
        var admin = AdminClientFor(factory, "admin-cl404");

        var resp = await admin.PostAsync("/admin/v1/disputes/case_does_not_exist/close", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Close_As_Non_Admin_Returns_403()
    {
        using var factory = new WebApplicationFactory<Program>();
        var caseId = await OpenCaseAsync(factory, "c-cl-na", "j-cl-na", "overcharged");
        var admin = AdminClientFor(factory, "admin-seed-cl-na");
        (await admin.PostAsJsonAsync($"/admin/v1/disputes/{caseId}/resolve", new ResolveCaseRequest
        {
            Decision = "no_action"
        })).EnsureSuccessStatusCode();

        var http = ClientFor(factory, "u-cl-na", Roles.Client);
        var resp = await http.PostAsync($"/admin/v1/disputes/{caseId}/close", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------
    // Escalate NotAParty → 403 (security ordering: a stranger naming a
    // real, already-disputed delivery is rejected as not-a-party FIRST,
    // never learning a dispute exists — closes an enumeration oracle).
    // ----------------------------------------------------------------
    [Fact]
    public async Task Escalate_By_Non_Party_Returns_403_Not_A_Party()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "owner-client", "owner-jeeber");

        var stranger = ClientFor(factory, "u-stranger");
        var resp = await stranger.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("not-a-party");
    }

    [Fact]
    public async Task Escalate_By_Non_Party_On_Already_Disputed_Delivery_Still_Returns_403_Not_A_Party()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "owner-client2", "owner-jeeber2");

        // The legitimate client opens a dispute first.
        var owner = ClientFor(factory, "owner-client2");
        (await owner.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "damaged_goods"
        })).EnsureSuccessStatusCode();

        // A stranger naming the SAME (already-disputed) delivery must STILL get
        // not-a-party (403), NOT a 409 already-escalated — otherwise the response
        // code leaks that a dispute exists (state-disclosure oracle). The IsParty
        // check runs BEFORE the active-case lookup precisely to close this.
        var stranger = ClientFor(factory, "u-stranger2");
        var resp = await stranger.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("not-a-party",
            "the not-a-party gate must precede the already-escalated check so a stranger cannot probe dispute existence");
    }

    [Fact]
    public async Task Escalate_By_Assigned_Jeeber_Is_Allowed()
    {
        using var factory = new WebApplicationFactory<Program>();
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, "client-jp", "jeeber-jp");

        // The assigned jeeber IS a party → may escalate.
        var jeeber = ClientFor(factory, "jeeber-jp", Roles.Jeeber);
        var resp = await jeeber.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "no_delivery"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        body!.OpenedByUserId.Should().Be("jeeber-jp");
        body.CounterpartyUserId.Should().Be("client-jp");
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

    /// <summary>Seeds a delivery with an assigned jeeber, escalates it as the client, returns the case id.</summary>
    private static async Task<string> OpenCaseAsync(
        WebApplicationFactory<Program> factory, string clientId, string jeeberId, string reason)
    {
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, clientId, jeeberId);
        var http = ClientFor(factory, clientId);
        var resp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = reason
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        return body!.Id;
    }

    private static async Task<string> SeedDeliveryWithJeeberAsync(
        WebApplicationFactory<Program> factory, string clientId, string jeeberId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "test delivery"
        }, CancellationToken.None);

        var lookup = await store.GetAsync(created.Id, CancellationToken.None);
        if (lookup is not null)
        {
            lookup.JeeberId = jeeberId;
        }
        return created.Id;
    }
}
