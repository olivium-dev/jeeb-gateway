using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.Kyc;
using JeebGateway.Push;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-005 / JEEB-23 acceptance criteria:
///   AC1. Queue returns pending submissions ordered by submission time.
///   AC2. Approve unlocks Jeeber role within 5 seconds.
///   AC3. Reject sends push with reason; user can resubmit.
///   AC4. Request-resubmit reopens specific document step only.
/// </summary>
public class AdminKycReviewEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminKycReviewEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Queue_Requires_Admin_Role()
    {
        var notAdmin = ClientFor("queue-no-admin");

        var resp = await notAdmin.GetAsync("/admin/kyc/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Queue_Without_Identity_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await anon.GetAsync("/admin/kyc/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Queue_Returns_Pending_Submissions_Ordered_By_SubmissionTime()
    {
        // AC1: the reviewer drains the queue oldest-first so users get
        // a fair-FIFO review experience.
        var first = await SeedSubmissionAsync("kycq-user-1", "FIRST-PLATE", DateTimeOffset.UtcNow.AddMinutes(-30));
        var middle = await SeedSubmissionAsync("kycq-user-2", "MID-PLATE", DateTimeOffset.UtcNow.AddMinutes(-20));
        var last = await SeedSubmissionAsync("kycq-user-3", "LAST-PLATE", DateTimeOffset.UtcNow.AddMinutes(-10));

        var admin = AdminClient("admin-q-1");
        var resp = await admin.GetAsync("/admin/kyc/queue?pageSize=50");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KycQueueResponse>();

        // Filter to only the three rows we seeded — other tests may have
        // left rows in the shared in-memory store.
        var ours = body!.Items.Where(i => i.Id == first.Id || i.Id == middle.Id || i.Id == last.Id).ToList();
        ours.Should().HaveCount(3);
        ours.Select(i => i.Id).Should().ContainInOrder(first.Id, middle.Id, last.Id);
        ours.Should().AllSatisfy(i => i.Status.Should().Be(KycStatus.PendingReview));
    }

    [Fact]
    public async Task Queue_Excludes_Finalised_Submissions()
    {
        // Once a row is approved/rejected it must leave the queue —
        // otherwise the reviewer keeps re-seeing decided submissions.
        var pending = await SeedSubmissionAsync("kycq-pending", "PEN-1", DateTimeOffset.UtcNow.AddMinutes(-5));
        var approved = await SeedSubmissionAsync("kycq-approved", "APP-1", DateTimeOffset.UtcNow.AddMinutes(-4));
        var rejected = await SeedSubmissionAsync("kycq-rejected", "REJ-1", DateTimeOffset.UtcNow.AddMinutes(-3));

        var admin = AdminClient("admin-q-2");
        (await admin.PatchAsJsonAsync($"/admin/kyc/{approved.Id}/review", new { action = "approve" }))
            .EnsureSuccessStatusCode();
        (await admin.PatchAsJsonAsync($"/admin/kyc/{rejected.Id}/review",
            new { action = "reject", reason = "blurry" })).EnsureSuccessStatusCode();

        var resp = await admin.GetAsync("/admin/kyc/queue?pageSize=100");
        var body = await resp.Content.ReadFromJsonAsync<KycQueueResponse>();

        body!.Items.Select(i => i.Id).Should().Contain(pending.Id);
        body.Items.Select(i => i.Id).Should().NotContain(approved.Id);
        body.Items.Select(i => i.Id).Should().NotContain(rejected.Id);
    }

    [Fact]
    public async Task Queue_Validates_Page_Parameters()
    {
        var admin = AdminClient("admin-q-3");

        (await admin.GetAsync("/admin/kyc/queue?page=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/admin/kyc/queue?pageSize=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/admin/kyc/queue?pageSize=999")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approve_Unlocks_Jeeber_Role_Within_5_Seconds()
    {
        // AC2: approve must grant the Jeeber role on the user within 5s.
        SeedUser("kyc-approve-user", roles: new[] { Roles.Client });
        var submission = await SeedSubmissionAsync("kyc-approve-user", "AP-1", DateTimeOffset.UtcNow.AddMinutes(-2));

        var admin = AdminClient("admin-approve-1");
        var sw = Stopwatch.StartNew();
        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review", new { action = "approve" });
        sw.Stop();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "AC2: approve must unlock the Jeeber role within 5 seconds");

        var body = await resp.Content.ReadFromJsonAsync<KycReviewResponse>();
        body!.Submission.Status.Should().Be(KycStatus.Verified);
        body.RoleGranted.Should().BeTrue();

        var users = _factory.Services.GetRequiredService<IUsersStore>();
        var updated = await users.GetByIdAsync("kyc-approve-user", default);
        updated!.Roles.Should().Contain(Roles.Jeeber);
    }

    [Fact]
    public async Task Approve_Is_Idempotent_On_Role_Even_If_User_Already_Has_It()
    {
        // Dual-role accounts (T-backend-041) can already be Jeebers. The
        // approve path must not re-add the role and must report
        // RoleGranted=false so the audit log is honest.
        SeedUser("kyc-approve-dual", roles: new[] { Roles.Client, Roles.Jeeber });
        var submission = await SeedSubmissionAsync("kyc-approve-dual", "AP-DUAL", DateTimeOffset.UtcNow.AddMinutes(-2));

        var admin = AdminClient("admin-approve-2");
        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review", new { action = "approve" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<KycReviewResponse>();
        body!.RoleGranted.Should().BeFalse();

        var users = _factory.Services.GetRequiredService<IUsersStore>();
        var updated = await users.GetByIdAsync("kyc-approve-dual", default);
        updated!.Roles.Count(r => string.Equals(r, Roles.Jeeber, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "GrantRole must be a set-add, not a list-append");
    }

    [Fact]
    public async Task Reject_Sends_Push_With_Reason_And_User_Can_Resubmit()
    {
        // AC3: rejection fires a push with the reason so the user knows
        // what to fix, and the gateway accepts a fresh /kyc/submit.
        SeedUser("kyc-reject-user", roles: new[] { Roles.Client });
        await RegisterDeviceAsync("kyc-reject-user");
        var submission = await SeedSubmissionAsync("kyc-reject-user", "RJ-1", DateTimeOffset.UtcNow.AddMinutes(-2));

        var admin = AdminClient("admin-reject-1");
        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review",
            new { action = "reject", reason = "selfie too dark" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KycReviewResponse>();
        body!.Submission.Status.Should().Be(KycStatus.Rejected);
        body.Submission.RejectionReason.Should().Be("selfie too dark");
        body.PushSent.Should().BeTrue();

        var fcm = _factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        var rejection = fcm.Sent.FirstOrDefault(s =>
            s.Request.UserId == "kyc-reject-user"
            && s.Request.Data is not null
            && s.Request.Data.TryGetValue("kyc_submission_id", out var sid)
            && sid == submission.Id);
        rejection.Should().NotBeNull("AC3: a push must land with the rejection reason");
        rejection!.Request.Trigger.Should().Be(NotificationTrigger.KycUpdate);
        rejection.Request.Body.Should().Contain("selfie too dark");
        rejection.Request.Data!["kyc_reason"].Should().Be("selfie too dark");
        rejection.Request.Data["kyc_action"].Should().Be("rejected");

        // AC3 (second clause): user can resubmit — POST /kyc/submit lands
        // a brand-new pending_review row.
        var user = ClientFor("kyc-reject-user");
        using var form = BuildValidForm();
        var resubmit = await user.PostAsync("/kyc/submit", form);
        resubmit.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var store = _factory.Services.GetRequiredService<IKycStore>();
        var latest = await store.GetLatestForUserAsync("kyc-reject-user", default);
        latest!.Status.Should().Be(KycStatus.PendingReview);
        latest.Id.Should().NotBe(submission.Id, "resubmission creates a new queue entry");
    }

    [Fact]
    public async Task Reject_Without_Reason_Returns_400()
    {
        var submission = await SeedSubmissionAsync("kyc-reject-noreason", "RJ-2", DateTimeOffset.UtcNow.AddMinutes(-2));
        var admin = AdminClient("admin-reject-2");

        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review",
            new { action = "reject", reason = "  " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RequestResubmit_Reopens_Only_Specific_Document_Step()
    {
        // AC4: request-resubmit names a subset of steps; the row carries
        // only those steps so the mobile UI reopens only those upload
        // fields (the others stay accepted).
        SeedUser("kyc-resub-user", roles: new[] { Roles.Client });
        await RegisterDeviceAsync("kyc-resub-user");
        var submission = await SeedSubmissionAsync("kyc-resub-user", "RS-1", DateTimeOffset.UtcNow.AddMinutes(-2));

        var admin = AdminClient("admin-resub-1");
        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review", new
        {
            action = "request_resubmit",
            reason = "selfie blurry, try again with better lighting",
            resubmitSteps = new[] { KycDocumentStep.Selfie }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KycReviewResponse>();
        body!.Submission.Status.Should().Be(KycStatus.ResubmitRequested);
        body.Submission.ResubmitSteps.Should().BeEquivalentTo(new[] { KycDocumentStep.Selfie });
        body.Submission.ResubmitSteps.Should().NotContain(KycDocumentStep.IdFront);
        body.Submission.ResubmitSteps.Should().NotContain(KycDocumentStep.IdBack);
        body.Submission.ResubmitSteps.Should().NotContain(KycDocumentStep.VehicleRegistration);

        // The user-facing GET /kyc/status surfaces the same subset so the
        // mobile app can reopen exactly that step in the UI.
        var asUser = ClientFor("kyc-resub-user");
        var status = await asUser.GetFromJsonAsync<KycStatusResponse>("/kyc/status");
        status!.Latest!.Status.Should().Be(KycStatus.ResubmitRequested);
        status.Latest.ResubmitSteps.Should().BeEquivalentTo(new[] { KycDocumentStep.Selfie });

        // And the push carries the same list so deep-link navigation works.
        var fcm = _factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        var sent = fcm.Sent.LastOrDefault(s =>
            s.Request.UserId == "kyc-resub-user"
            && s.Request.Data is not null
            && s.Request.Data.TryGetValue("kyc_action", out var a) && a == "resubmit_requested");
        sent.Should().NotBeNull();
        sent!.Request.Data!["kyc_resubmit_steps"].Should().Be(KycDocumentStep.Selfie);
    }

    [Fact]
    public async Task RequestResubmit_Without_Steps_Returns_400()
    {
        var submission = await SeedSubmissionAsync("kyc-resub-empty", "RS-EMPTY", DateTimeOffset.UtcNow.AddMinutes(-2));
        var admin = AdminClient("admin-resub-2");

        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review",
            new { action = "request_resubmit", reason = "x", resubmitSteps = Array.Empty<string>() });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RequestResubmit_With_Unknown_Step_Returns_400()
    {
        var submission = await SeedSubmissionAsync("kyc-resub-bad", "RS-BAD", DateTimeOffset.UtcNow.AddMinutes(-2));
        var admin = AdminClient("admin-resub-3");

        var resp = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review",
            new { action = "request_resubmit", reason = "x", resubmitSteps = new[] { "id_front", "not_a_step" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Review_Already_Finalised_Submission_Returns_409()
    {
        // Two reviewers can race on the queue; the second one must hit
        // 409 rather than silently overwriting the first decision.
        SeedUser("kyc-race-user", roles: new[] { Roles.Client });
        var submission = await SeedSubmissionAsync("kyc-race-user", "RACE-1", DateTimeOffset.UtcNow.AddMinutes(-2));

        var admin = AdminClient("admin-race-1");
        (await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review", new { action = "approve" }))
            .EnsureSuccessStatusCode();

        var second = await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review",
            new { action = "reject", reason = "race" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Review_Unknown_Submission_Returns_404()
    {
        var admin = AdminClient("admin-404-1");

        var resp = await admin.PatchAsJsonAsync("/admin/kyc/does-not-exist/review",
            new { action = "approve" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Review_Without_Body_Returns_400()
    {
        var submission = await SeedSubmissionAsync("kyc-nobody", "NB-1", DateTimeOffset.UtcNow.AddMinutes(-2));
        var admin = AdminClient("admin-nobody-1");

        var resp = await admin.PatchAsync($"/admin/kyc/{submission.Id}/review", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Review_Without_Admin_Role_Returns_403()
    {
        var submission = await SeedSubmissionAsync("kyc-noadmin", "NA-1", DateTimeOffset.UtcNow.AddMinutes(-2));
        var notAdmin = ClientFor("not-admin");

        var resp = await notAdmin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review",
            new { action = "approve" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Review_Action_Is_Audited_With_Admin_And_Timestamp()
    {
        // Mirrors T-backend-030 audit invariants — every admin mutation
        // must land in admin_actions with the admin id and timestamp.
        SeedUser("kyc-audit-user", roles: new[] { Roles.Client });
        var submission = await SeedSubmissionAsync("kyc-audit-user", "AU-1", DateTimeOffset.UtcNow.AddMinutes(-2));

        var admin = AdminClient("admin-audit-1");
        (await admin.PatchAsJsonAsync($"/admin/kyc/{submission.Id}/review", new { action = "approve" }))
            .EnsureSuccessStatusCode();

        var auditLog = _factory.Services.GetRequiredService<IAdminAuditLog>();
        var entries = await auditLog.ListForEntityAsync("kyc_submission", submission.Id, default);
        entries.Should().ContainSingle()
            .Which.AdminUserId.Should().Be("admin-audit-1");
        entries.Single().Action.Should().Be("approve_kyc");
        entries.Single().CreatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<KycSubmission> SeedSubmissionAsync(
        string userId,
        string vehicleRegistration,
        DateTimeOffset submittedAt)
    {
        var store = _factory.Services.GetRequiredService<IKycStore>();
        return await store.AddAsync(new KycSubmission
        {
            Id = $"kyc_seed_{Guid.NewGuid():N}",
            UserId = userId,
            Status = KycStatus.PendingReview,
            SubmittedAt = submittedAt,
            VehicleType = "motorcycle",
            VehicleRegistration = vehicleRegistration,
            IdFrontDocumentId = $"doc_{Guid.NewGuid():N}",
            IdBackDocumentId = $"doc_{Guid.NewGuid():N}",
            SelfieDocumentId = $"doc_{Guid.NewGuid():N}",
            LivenessPassed = true
        }, default);
    }

    private void SeedUser(string id, string[] roles)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(new UserProfile
        {
            Id = id,
            Phone = "+96550000000",
            Name = "KYC Test User",
            Roles = roles.ToList(),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
    }

    private async Task RegisterDeviceAsync(string userId)
    {
        var devices = _factory.Services.GetRequiredService<IDeviceTokenStore>();
        await devices.RegisterAsync(new DeviceToken(userId, DevicePlatform.Fcm, $"tok-{userId}"), default);
    }

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private HttpClient AdminClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private static MultipartFormDataContent BuildValidForm()
    {
        return new MultipartFormDataContent
        {
            { ImagePart(), "idFront", "front.jpg" },
            { ImagePart(), "idBack", "back.jpg" },
            { ImagePart(), "selfie", "selfie.jpg" },
            { new StringContent("motorcycle"), "vehicleType" },
            { new StringContent("KW-RESUB"), "vehicleRegistration" }
        };
    }

    private static ByteArrayContent ImagePart()
    {
        var part = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9, 0xAA, 0xBB, 0xCC, 0xDD });
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return part;
    }
}
