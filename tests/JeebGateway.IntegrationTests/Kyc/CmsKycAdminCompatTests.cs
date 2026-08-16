using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Kyc;

/// <summary>
/// D3 — the CMS-compat KYC admin surface at <c>/user-management/admin/kyc</c>, the routes the
/// DEPLOYED back-office KYC micro-frontend actually calls. The gateway routed none of them, so
/// the CMS KYC section failed at the LIST call (the wave-1 "?q= ignored" report understated it:
/// the probed <c>/admin/kyc/queue</c> is a different route the CMS never calls).
///
/// <para>Review here MUST behave identically to <c>PATCH /admin/kyc/{id}/review</c> — same
/// whitelist, same user-management role append, same audit entry — because both routes run the
/// one extracted composer. The approve test asserts the role grant the native route's tests
/// assert, which is what makes "cannot fork" checkable.</para>
/// </summary>
public sealed class CmsKycAdminCompatTests : IClassFixture<CmsKycAdminCompatTests.CompatFactory>
{
    private const string Route = "/user-management/admin/kyc";

    private readonly CompatFactory _factory;

    public CmsKycAdminCompatTests(CompatFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task List_Returns_The_Paged_Envelope_With_Decorated_Rows()
    {
        var id = await SubmitAsync("cms-list-applicant", "Nour Haddad", "+96171111001");

        var resp = await Admin().GetAsync($"{Route}?status=pending&page=1&pageSize=50");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await ReadJsonAsync(resp);
        page.GetProperty("page").GetInt32().Should().Be(1);
        page.GetProperty("pageSize").GetInt32().Should().Be(50);
        page.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
        page.GetProperty("totalPages").GetInt32().Should().BeGreaterThan(0);

        var row = page.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == id);
        row.GetProperty("userId").GetString().Should().Be("cms-list-applicant");
        row.GetProperty("userName").GetString().Should().Be("Nour Haddad");
        row.GetProperty("phone").GetString().Should().Be("+96171111001");
        row.GetProperty("templateId").GetString().Should().Be("jeeb_jeeber_v1");
        row.GetProperty("status").GetString().Should().Be("pending");
        row.GetProperty("submittedAt").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task List_q_Filters_By_Name_And_By_Phone_Case_Insensitively()
    {
        var wanted = await SubmitAsync("cms-q-target", "Zeina Karam", "+96176543210");
        await SubmitAsync("cms-q-other", "Bilal Fares", "+96170000999");

        var byName = await ItemIdsAsync($"{Route}?q=zeina");
        byName.Should().Contain(wanted).And.HaveCount(1, "the q filter must exclude non-matching rows");

        var byPhone = await ItemIdsAsync($"{Route}?q=76543");
        byPhone.Should().Contain(wanted).And.HaveCount(1);

        var byUpper = await ItemIdsAsync($"{Route}?q=KARAM");
        byUpper.Should().Contain(wanted, "the match is case-insensitive");
    }

    [Fact]
    public async Task List_q_With_No_Match_Is_200_With_An_Empty_Page()
    {
        await SubmitAsync("cms-q-nomatch", "Sami Aoun", "+96171222333");

        var resp = await Admin().GetAsync($"{Route}?q=zzz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await ReadJsonAsync(resp);
        page.GetProperty("items").GetArrayLength().Should().Be(0);
        page.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task List_Status_Approved_Is_An_Honest_Empty_Page_Not_The_Pending_Queue()
    {
        await SubmitAsync("cms-approved-filter", "Hala Nassar", "+96171444555");

        var page = await ReadJsonAsync(await Admin().GetAsync($"{Route}?status=approved"));

        page.GetProperty("items").GetArrayLength().Should().Be(0,
            "kyc-service can only list the pending queue — answering approved from it would be a lie");
        page.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Detail_Returns_The_Submission_With_The_Embedded_User_Summary()
    {
        var id = await SubmitAsync("cms-detail-applicant", "Rami Saad", "+96171777888");

        var resp = await Admin().GetAsync($"{Route}/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await ReadJsonAsync(resp);
        detail.GetProperty("id").GetString().Should().Be(id);
        detail.GetProperty("userId").GetString().Should().Be("cms-detail-applicant");
        detail.GetProperty("templateId").GetString().Should().Be("jeeb_jeeber_v1");
        detail.GetProperty("status").GetString().Should().Be("pending");
        detail.GetProperty("user").GetProperty("name").GetString().Should().Be("Rami Saad");
        detail.GetProperty("user").GetProperty("phone").GetString().Should().Be("+96171777888");
    }

    [Fact]
    public async Task Detail_Of_An_Unknown_Submission_Is_404()
    {
        var resp = await Admin().GetAsync($"{Route}/sub_does_not_exist");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approve_Flips_The_Status_And_Records_The_Same_Role_Grant_Audit_As_The_Native_Route()
    {
        var id = await SubmitAsync("cms-approve-applicant", "Karim Zein", "+96171999000");

        var resp = await Admin().PatchAsync($"{Route}/{id}", JsonContent.Create(new { action = "approve" }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadJsonAsync(resp)).GetProperty("status").GetString().Should().Be("approved");

        // The shared composer's side effects: the same audit action + role_granted the native
        // PATCH /admin/kyc/{id}/review writes. Asserting these is what makes "cannot fork" real.
        var audit = await _factory.Services.GetRequiredService<IAdminAuditLog>()
            .ListForEntityAsync("kyc_submission", id, default);
        audit.Should().ContainSingle();
        audit[0].Action.Should().Be("approve_kyc");
        audit[0].AdminUserId.Should().Be("cms-kyc-admin");
        audit[0].AfterState!["role_granted"].Should().Be(true);
        audit[0].AfterState!["status"].Should().Be("Verified");
    }

    [Fact]
    public async Task Reject_Surfaces_The_Reason_As_reviewReason()
    {
        var id = await SubmitAsync("cms-reject-applicant", "Maya Chidiac", "+96171555666");

        var resp = await Admin().PatchAsync(
            $"{Route}/{id}", JsonContent.Create(new { action = "reject", reason = "id document unreadable" }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await ReadJsonAsync(resp);
        detail.GetProperty("status").GetString().Should().Be("rejected");
        detail.GetProperty("reviewReason").GetString().Should().Be("id document unreadable");
    }

    [Fact]
    public async Task Re_Review_Of_A_Finalised_Submission_Is_409()
    {
        var id = await SubmitAsync("cms-conflict-applicant", "Omar Daher", "+96171888777");

        (await Admin().PatchAsync($"{Route}/{id}", JsonContent.Create(new { action = "approve" })))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Admin().PatchAsync($"{Route}/{id}", JsonContent.Create(new { action = "approve" }));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_Client_Token_Is_403_On_Every_Compat_Route_Never_401_Never_200()
    {
        var id = await SubmitAsync("cms-rbac-applicant", "Jad Aziz", "+96171333222");
        var client = Identified(_factory.CreateClient(), "cms-rbac-client", "client");

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PatchAsync($"{Route}/{id}", JsonContent.Create(new { action = "approve" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----- native /admin/kyc/queue: additive q + the fields it filters on ------

    [Fact]
    public async Task Native_Queue_Carries_userName_And_phone_And_Honours_q()
    {
        var id = await SubmitAsync("native-q-target", "Farid Mansour", "+96170112233");
        await SubmitAsync("native-q-other", "Rita Semaan", "+96170998877");

        var unfiltered = await Admin().GetAsync("/admin/kyc/queue?page=1&pageSize=100");
        unfiltered.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await ReadJsonAsync(unfiltered)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == id);
        row.GetProperty("userName").GetString().Should().Be("Farid Mansour");
        row.GetProperty("phone").GetString().Should().Be("+96170112233");

        var filtered = await ReadJsonAsync(await Admin().GetAsync("/admin/kyc/queue?q=mansour"));
        filtered.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString())
            .Should().Contain(id).And.HaveCount(1);

        // The original defect's probe, inverted: q=zzz must NOT return the unfiltered body.
        var none = await ReadJsonAsync(await Admin().GetAsync("/admin/kyc/queue?q=zzz"));
        none.GetProperty("items").GetArrayLength().Should().Be(0);
        none.GetProperty("total").GetInt32().Should().Be(0);
    }

    // ----- helpers -----------------------------------------------------------

    private HttpClient Admin() => Identified(_factory.CreateClient(), "cms-kyc-admin", "admin");

    private static HttpClient Identified(HttpClient client, string userId, string role)
    {
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    private async Task<List<string?>> ItemIdsAsync(string url)
    {
        var resp = await Admin().GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadJsonAsync(resp)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString())
            .ToList();
    }

    /// <summary>Seeds the user projection (name/phone the join reads) then submits a KYC package.</summary>
    private async Task<string> SubmitAsync(string userId, string name, string phone)
    {
        _factory.Services.GetRequiredService<InMemoryUsersStore>().Seed(new UserProfile
        {
            Id = userId,
            Phone = phone,
            Name = name,
            Language = "en",
            Roles = new List<string> { Roles.Client },
            ActiveRole = Roles.Client,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var applicant = Identified(_factory.CreateClient(), userId, "client");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/kyc/submit")
        {
            Content = JsonContent.Create(new
            {
                id_type = "national_id",
                id_number = "123456789012",
                id_document_front_url = "cdn://obj/front",
                id_document_back_url = "cdn://obj/back",
                driver_license_number = "DL-11223344",
                driver_license_expiry = "2030-01-01",
                vehicle_registration_url = "cdn://obj/vehreg",
                vehicle_plate_number = "XYZ-9876",
                vehicle_year_make_model = "2022 Honda Civic",
                selfie_with_liveness_url = "cdn://obj/selfie",
                tos_accepted_version = "v1",
            }),
        };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var resp = await applicant.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadJsonAsync(resp)).GetProperty("submissionId").GetString()!;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The gateway with the KYC upstream ON behind a stateful fake kyc-service (the same
    /// production seam path the native admin tests exercise).
    /// </summary>
    public sealed class CompatFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FeatureFlags:UseUpstream:Kyc", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKycServiceClient>();
                services.AddSingleton<IKycServiceClient, CompatFakeKycService>();
            });
        }
    }

    /// <summary>
    /// Test-only stand-in for the owning kyc-service: Submitted rows are the pending queue,
    /// review finalises exactly once (409 after), reject needs a reason, approve emits the
    /// jeeber grant intent.
    /// </summary>
    private sealed class CompatFakeKycService : IKycServiceClient
    {
        private sealed class Row
        {
            public required string Id { get; init; }
            public required string UserId { get; init; }
            public string Status { get; set; } = "Submitted";
            public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
            public DateTimeOffset? ReviewedAt { get; set; }
            public string? RejectionReason { get; set; }
        }

        private readonly ConcurrentDictionary<string, string> _idByKey = new();
        private readonly ConcurrentDictionary<string, Row> _rows = new();
        private readonly object _gate = new();

        public Task<KycSubmitResult> SubmitAsync(
            KycSubmitUpstreamPayload payload, string idempotencyKey, CancellationToken ct)
        {
            var replayed = _idByKey.ContainsKey(idempotencyKey);
            var id = _idByKey.GetOrAdd(idempotencyKey, _ =>
            {
                var newId = "sub_" + Guid.NewGuid().ToString("N")[..12];
                _rows[newId] = new Row { Id = newId, UserId = payload.UserId };
                return newId;
            });
            return Task.FromResult(new KycSubmitResult
            {
                SubmissionId = id,
                State = _rows[id].Status,
                TosAcceptedVersion = payload.TosAcceptedVersion,
                Replayed = replayed,
            });
        }

        public Task<KycTosSignatureResult> StampTosSignatureAsync(
            string submissionId, KycTosStampPayload payload, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(new KycTosSignatureResult
            {
                TosSignedAt = DateTimeOffset.UtcNow,
                TosAcceptedVersion = payload.TosAcceptedVersion,
            });

        public Task<KycTosSignatureResult> StampStandaloneTosAsync(
            string userId, KycTosStampPayload payload, CancellationToken ct)
            => Task.FromResult(new KycTosSignatureResult
            {
                TosSignedAt = DateTimeOffset.UtcNow,
                TosAcceptedVersion = payload.TosAcceptedVersion,
            });

        public Task<KycSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
            => Task.FromResult(_rows.Values
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.SubmittedAt)
                .Select(ToView)
                .FirstOrDefault());

        public Task<KycSubmissionView?> GetByIdAsync(string submissionId, CancellationToken ct)
            => Task.FromResult(_rows.TryGetValue(submissionId, out var row) ? ToView(row) : null);

        public Task<KycQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
        {
            var pending = _rows.Values
                .Where(r => r.Status == "Submitted")
                .OrderBy(r => r.SubmittedAt)
                .ToList();
            return Task.FromResult(new KycQueuePage
            {
                Items = pending.Skip((page - 1) * pageSize).Take(pageSize).Select(ToView).ToList(),
                Page = page,
                PageSize = pageSize,
                Total = pending.Count,
            });
        }

        public Task<KycReviewDecision> ReviewAsync(
            string submissionId, KycReviewDecisionRequest request, CancellationToken ct)
        {
            if (!_rows.TryGetValue(submissionId, out var row))
            {
                throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            }

            lock (_gate)
            {
                if (row.Status != "Submitted") throw new KycReviewConflictException(submissionId, null);

                switch (request.Action)
                {
                    case KycReviewActionKind.Approve:
                        row.Status = "Verified";
                        row.ReviewedAt = DateTimeOffset.UtcNow;
                        return Task.FromResult(Decision(row, "jeeber"));

                    case KycReviewActionKind.Reject:
                        if (string.IsNullOrWhiteSpace(request.Reason))
                        {
                            throw new KycReviewValidationException("reject requires a reason.");
                        }
                        row.Status = "Rejected";
                        row.RejectionReason = request.Reason;
                        row.ReviewedAt = DateTimeOffset.UtcNow;
                        return Task.FromResult(Decision(row, null));

                    default:
                        throw new KycReviewValidationException($"unknown action {request.Action}.");
                }
            }
        }

        private static KycReviewDecision Decision(Row row, string? grantsRole) => new()
        {
            SubmissionId = row.Id,
            UserId = row.UserId,
            Status = row.Status,
            RejectionReason = row.RejectionReason,
            GrantsRole = grantsRole,
        };

        private static KycSubmissionView ToView(Row row) => new()
        {
            SubmissionId = row.Id,
            UserId = row.UserId,
            Status = row.Status,
            SubmittedAt = row.SubmittedAt,
            ReviewedAt = row.ReviewedAt,
            RejectionReason = row.RejectionReason,
        };
    }
}
