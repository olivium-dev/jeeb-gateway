using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Kyc;

/// <summary>
/// JEB-1522 / Plan-3 WS-P5 (CTO decision D5) — the KYC MANUAL admin-approval
/// flow, end-to-end at the gateway: submit → pending queue → admin review
/// (approve / reject) → applicant status, plus the two decisive adversarial
/// probes:
/// <list type="bullet">
///   <item><b>RBAC (T4):</b> a client-role caller on the admin endpoints is a
///     Layer-2 403 (never 401, never 200).</item>
///   <item><b>No WI-4 dependency (T6):</b> with auto-provisioning absent and the
///     KYC upstream flag OFF, every KYC route fails CLOSED as a typed 503
///     ProblemDetails — never a 500 from a missing WI-4 broker.</item>
/// </list>
///
/// These run against the PRODUCTION seam path (KycBffSeam → IKycServiceClient)
/// with a stateful fake standing in for the owning kyc-service. The fake honours
/// the SM-6 manual-queue semantics (Submitted = pending review; Verified /
/// Rejected are final; re-review of a finalised row → 409 conflict; reject
/// requires a reason → 400), so the gateway-side transitions, idempotency and
/// status surfacing are exercised for real — nothing is WI-4-provisioned.
/// </summary>
public sealed class KycManualApprovalFlowTests : IClassFixture<KycManualApprovalFlowTests.ManualQueueFactory>
{
    private readonly ManualQueueFactory _factory;

    public KycManualApprovalFlowTests(ManualQueueFactory factory)
    {
        _factory = factory;
    }

    // ----- T1: submit creates a pending submission visible in the admin queue -----

    [Fact]
    public async Task T1_Submit_Creates_Pending_Submission_That_Appears_In_Admin_Queue()
    {
        var applicant = ClientFor("t1-applicant", "client");

        var submit = await PostJsonAsync(applicant, "/v1/kyc/submit", SamplePackage(), Guid.NewGuid().ToString("N"));
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        var submitted = await ReadJsonAsync(submit);
        var submissionId = submitted.GetProperty("submissionId").GetString();
        submissionId.Should().NotBeNullOrWhiteSpace();
        submitted.GetProperty("state").GetString().Should().Be("Submitted"); // SM-6 pending-review state

        // The submission is queued for MANUAL review — visible to the admin.
        var admin = ClientFor("t1-admin", "admin");
        var queue = await admin.GetAsync("/admin/kyc/queue?page=1&pageSize=50");
        queue.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await ReadJsonAsync(queue);
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString())
            .ToList();
        ids.Should().Contain(submissionId);

        // The applicant sees the pending state on the status surface.
        var status = await applicant.GetAsync("/v1/kyc/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(status)).GetProperty("state").GetString().Should().Be("Submitted");
    }

    // ----- T2: admin approve → approved status, reflected to the applicant -----

    [Fact]
    public async Task T2_Admin_Approve_Finalises_Submission_And_Applicant_Status_Reflects_It()
    {
        var applicant = ClientFor("t2-applicant", "client");
        var submissionId = await SubmitAsync(applicant);

        var admin = ClientFor("t2-admin", "admin");
        var review = await admin.PatchAsync($"/admin/kyc/{submissionId}/review",
            JsonContent.Create(new { action = "approve" }));

        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(review);
        body.GetProperty("submission").GetProperty("status").GetString().Should().Be("Verified");
        body.GetProperty("roleGranted").GetBoolean().Should().BeTrue();

        var status = await applicant.GetAsync("/v1/kyc/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(status)).GetProperty("state").GetString().Should().Be("Verified");
    }

    // ----- T3: admin reject with reason → rejected status + reason surfaced -----

    [Fact]
    public async Task T3_Admin_Reject_With_Reason_Surfaces_Rejection_To_Applicant()
    {
        var applicant = ClientFor("t3-applicant", "client");
        var submissionId = await SubmitAsync(applicant);

        var admin = ClientFor("t3-admin", "admin");
        var review = await admin.PatchAsync($"/admin/kyc/{submissionId}/review",
            JsonContent.Create(new { action = "reject", reason = "id document unreadable" }));

        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(review);
        body.GetProperty("submission").GetProperty("status").GetString().Should().Be("Rejected");
        body.GetProperty("submission").GetProperty("rejectionReason").GetString()
            .Should().Be("id document unreadable");

        var status = await applicant.GetAsync("/v1/kyc/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await ReadJsonAsync(status);
        view.GetProperty("state").GetString().Should().Be("Rejected");
        view.GetProperty("rejection_reason").GetString().Should().Be("id document unreadable");
    }

    [Fact]
    public async Task T3b_Reject_Without_Reason_Is_400_Validation_Not_500()
    {
        var applicant = ClientFor("t3b-applicant", "client");
        var submissionId = await SubmitAsync(applicant);

        var admin = ClientFor("t3b-admin", "admin");
        var review = await admin.PatchAsync($"/admin/kyc/{submissionId}/review",
            JsonContent.Create(new { action = "reject" }));

        review.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ----- T4: RBAC — client on the admin endpoints is 403, never 401/200 -----

    [Fact]
    public async Task T4_Client_Token_On_Admin_Queue_Is_403_Not_401()
    {
        var client = ClientFor("t4-client", "client");

        var resp = await client.GetAsync("/admin/kyc/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an authenticated client lacks kyc.review (L2 deny) — must be 403, not a Layer-1 401");
    }

    [Fact]
    public async Task T4b_Client_Token_On_Admin_Review_Is_403_Not_401()
    {
        var applicant = ClientFor("t4b-applicant", "client");
        var submissionId = await SubmitAsync(applicant);

        var resp = await applicant.PatchAsync($"/admin/kyc/{submissionId}/review",
            JsonContent.Create(new { action = "approve" }));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a client must never be able to approve their own (or any) KYC submission");
    }

    // ----- T5: idempotent review — second decision on a finalised row is 409 -----

    [Fact]
    public async Task T5_Second_Review_Of_Finalised_Submission_Is_409_Never_Double_Applied()
    {
        var applicant = ClientFor("t5-applicant", "client");
        var submissionId = await SubmitAsync(applicant);

        var admin = ClientFor("t5-admin", "admin");
        var first = await admin.PatchAsync($"/admin/kyc/{submissionId}/review",
            JsonContent.Create(new { action = "approve" }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await admin.PatchAsync($"/admin/kyc/{submissionId}/review",
            JsonContent.Create(new { action = "approve" }));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "N8: re-review of a finalised row is a 409");

        // The decision was not double-applied: status remains the first outcome.
        var status = await applicant.GetAsync("/v1/kyc/status");
        (await ReadJsonAsync(status)).GetProperty("state").GetString().Should().Be("Verified");
    }

    [Fact]
    public async Task T5b_Review_Of_Unknown_Submission_Is_404()
    {
        var admin = ClientFor("t5b-admin", "admin");

        var resp = await admin.PatchAsync("/admin/kyc/sub_does_not_exist/review",
            JsonContent.Create(new { action = "approve" }));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- T6: no WI-4 dependency — flag-off path fails CLOSED 503, never 500 -----

    public sealed class NoUpstreamFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // WI-4 auto-provisioning absent AND the KYC upstream off: nothing is
            // provisioned, nothing is reachable. The gateway must fail closed.
            builder.UseSetting("FeatureFlags:UseUpstream:Kyc", "false");
        }
    }

    [Fact]
    public async Task T6_All_Kyc_Routes_With_No_Upstream_And_No_WI4_Are_503_ProblemDetails_Never_500()
    {
        using var factory = new NoUpstreamFactory();

        var applicant = WithIdentity(factory.CreateClient(), "t6-applicant", "client");
        var admin = WithIdentity(factory.CreateClient(), "t6-admin", "admin");

        var probes = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("submit", () => PostJsonAsync(applicant, "/v1/kyc/submit", SamplePackage(), Guid.NewGuid().ToString("N"))),
            ("status", () => applicant.GetAsync("/v1/kyc/status")),
            ("queue", () => admin.GetAsync("/admin/kyc/queue")),
            ("review", () => admin.PatchAsync("/admin/kyc/sub_x/review", JsonContent.Create(new { action = "approve" }))),
        };

        foreach (var (name, call) in probes)
        {
            var resp = await call();
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                $"the no-WI-4 '{name}' path must fail closed as a typed 503, never a 500");
            var json = await ReadJsonAsync(resp);
            json.GetProperty("status").GetInt32().Should().Be(503);
        }
    }

    // ----- helpers -----------------------------------------------------------

    private async Task<string> SubmitAsync(HttpClient applicant)
    {
        var resp = await PostJsonAsync(applicant, "/v1/kyc/submit", SamplePackage(), Guid.NewGuid().ToString("N"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadJsonAsync(resp)).GetProperty("submissionId").GetString()!;
    }

    private HttpClient ClientFor(string userId, string role) =>
        WithIdentity(_factory.CreateClient(), userId, role);

    private static HttpClient WithIdentity(HttpClient client, string userId, string role)
    {
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    private static object SamplePackage() => new
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
    };

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string path, object body, string idempotencyKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Boots the gateway with FeatureFlags:UseUpstream:Kyc ON and a STATEFUL fake
    /// kyc-service behind the production seam. The fake implements the manual
    /// queue for real: submissions persist as Submitted (pending review), the
    /// queue lists only pending rows oldest-first, review finalises exactly once
    /// (409 on re-review), reject requires a reason (400), approve emits the
    /// grantsRole intent. No WI-4 auto-provisioning exists anywhere in this path.
    /// </summary>
    public sealed class ManualQueueFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Kyc", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKycServiceClient>();
                services.AddSingleton<IKycServiceClient, ManualQueueFakeKycService>();
            });
        }
    }

    /// <summary>
    /// Test-assembly-only stand-in for the owning kyc-service implementing the
    /// SM-6 manual review queue (Submitted → Verified | Rejected). Never shipped
    /// in the gateway (P9: named *Fake*, lives in tests only).
    /// </summary>
    private sealed class ManualQueueFakeKycService : IKycServiceClient
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

        private readonly ConcurrentDictionary<string, string> _idByIdempotencyKey = new();
        private readonly ConcurrentDictionary<string, Row> _rows = new();
        private readonly object _reviewGate = new();

        public Task<KycSubmitResult> SubmitAsync(KycSubmitUpstreamPayload payload, string idempotencyKey, CancellationToken ct)
        {
            var replayed = _idByIdempotencyKey.ContainsKey(idempotencyKey);
            var id = _idByIdempotencyKey.GetOrAdd(idempotencyKey, _ =>
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

        public Task<KycTosSignatureResult> StampTosSignatureAsync(string submissionId, KycTosStampPayload payload, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(new KycTosSignatureResult
            {
                TosSignedAt = DateTimeOffset.UtcNow,
                TosAcceptedVersion = payload.TosAcceptedVersion,
            });

        public Task<KycTosSignatureResult> StampStandaloneTosAsync(string userId, KycTosStampPayload payload, CancellationToken ct)
            => Task.FromResult(new KycTosSignatureResult
            {
                TosSignedAt = DateTimeOffset.UtcNow,
                TosAcceptedVersion = payload.TosAcceptedVersion,
            });

        public Task<KycSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
        {
            var latest = _rows.Values
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.SubmittedAt)
                .FirstOrDefault();
            return Task.FromResult(latest is null ? null : ToView(latest));
        }

        public Task<KycSubmissionView?> GetByIdAsync(string submissionId, CancellationToken ct)
            => Task.FromResult(_rows.TryGetValue(submissionId, out var row) ? ToView(row) : null);

        public Task<KycQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
        {
            var pending = _rows.Values
                .Where(r => r.Status == "Submitted")
                .OrderBy(r => r.SubmittedAt)
                .ToList();
            var items = pending
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(ToView)
                .ToList();
            return Task.FromResult(new KycQueuePage
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                Total = pending.Count,
            });
        }

        public Task<KycReviewDecision> ReviewAsync(string submissionId, KycReviewDecisionRequest request, CancellationToken ct)
        {
            if (!_rows.TryGetValue(submissionId, out var row))
            {
                // The gateway maps an upstream 404 via KycServiceClient to
                // KycBffNotFoundException; the fake mirrors that with the same
                // HttpRequestException(404) shape the real client translates from.
                throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            }

            lock (_reviewGate)
            {
                if (row.Status != "Submitted")
                {
                    throw new KycReviewConflictException(submissionId, null); // N8 — already finalised
                }

                switch (request.Action)
                {
                    case KycReviewActionKind.Approve:
                        row.Status = "Verified";
                        row.ReviewedAt = DateTimeOffset.UtcNow;
                        return Task.FromResult(Decision(row, grantsRole: "jeeber"));

                    case KycReviewActionKind.Reject:
                        if (string.IsNullOrWhiteSpace(request.Reason))
                        {
                            throw new KycReviewValidationException("reject requires a reason.");
                        }
                        row.Status = "Rejected";
                        row.RejectionReason = request.Reason;
                        row.ReviewedAt = DateTimeOffset.UtcNow;
                        return Task.FromResult(Decision(row, grantsRole: null));

                    case KycReviewActionKind.RequestResubmit:
                        row.Status = "ResubmitRequested";
                        row.ReviewedAt = DateTimeOffset.UtcNow;
                        return Task.FromResult(Decision(row, grantsRole: null));

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
