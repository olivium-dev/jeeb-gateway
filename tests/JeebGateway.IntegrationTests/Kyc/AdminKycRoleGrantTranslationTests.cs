using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Kyc;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Kyc;

/// <summary>
/// S03 H8 — the KYC-approve → role-switch chain root-fix. On approve the gateway
/// composes the user-management role append from the KYC grant INTENT, which is the
/// frozen Jeeb CONTRACT role (<c>jeeber</c>). UM persists OPAQUE roles
/// (<c>{customer,driver}</c>) and a later role/switch translates jeeber → driver
/// before checking available_roles. So the append MUST be translated to the OPAQUE
/// role too — otherwise UM stores "jeeber" while the switch looks for "driver" and
/// 403s. These tests pin that the role passed to UM on approve is the OPAQUE
/// <c>driver</c>, not the contract <c>jeeber</c>, and that roleGranted=true.
/// </summary>
public sealed class AdminKycRoleGrantTranslationTests
{
    [Fact]
    public async Task Approve_Appends_OPAQUE_Role_To_UserManagement_Not_Contract_Jeeber()
    {
        var seam = new StubKycSeam
        {
            ReviewOutcome = new KycBffReviewResult
            {
                SubmissionId = "sub-1",
                UserId = "applicant-1",
                Status = "Verified",
                GrantsRole = JeebRoleTranslator.ContractJeeber, // "jeeber" (contract)
            }
        };
        var um = new CapturingUm();

        using var factory = MakeFactory(seam, um);
        var admin = AdminClient(factory, "admin-1");

        var resp = await admin.PatchAsync("/admin/kyc/sub-1/review",
            JsonContent.Create(new { action = "approve" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ReviewResponseProbe>();
        body!.RoleGranted.Should().BeTrue();

        // THE FIX: the role appended in UM is the OPAQUE driver, NOT the contract jeeber.
        um.LastUserId.Should().Be("applicant-1");
        um.LastRole.Should().Be(Roles.Jeeber);          // "driver"
        um.LastRole.Should().NotBe(JeebRoleTranslator.ContractJeeber); // not "jeeber"
    }

    [Fact]
    public async Task Reject_Does_Not_Append_Any_Role()
    {
        var seam = new StubKycSeam
        {
            ReviewOutcome = new KycBffReviewResult
            {
                SubmissionId = "sub-2",
                UserId = "applicant-2",
                Status = "Rejected",
                RejectionReason = "blurry id",
                GrantsRole = null, // no grant on reject
            }
        };
        var um = new CapturingUm();

        using var factory = MakeFactory(seam, um);
        var admin = AdminClient(factory, "admin-2");

        var resp = await admin.PatchAsync("/admin/kyc/sub-2/review",
            JsonContent.Create(new { action = "reject", reason = "blurry id" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        um.AppendCalls.Should().Be(0);
    }

    // ----- harness -----

    private static WebApplicationFactory<Program> MakeFactory(
        IKycBffSeam seam, IUserManagementDualRoleClient um) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKycBffSeam>();
                services.AddSingleton(seam);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton(um);
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Kyc = true;
                    f.UserManagement = true;
                });
            });
        });

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private sealed class ReviewResponseProbe
    {
        public bool RoleGranted { get; set; }
        public bool PushSent { get; set; }
    }

    private sealed class CapturingUm : IUserManagementDualRoleClient
    {
        public int AppendCalls { get; private set; }
        public string? LastUserId { get; private set; }
        public string? LastRole { get; private set; }

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            AppendCalls++;
            LastUserId = userId;
            LastRole = opaqueRole;
            return Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, true));
        }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => Task.FromResult(new PhoneFindOrCreateResult(phone, false, new[] { Roles.Client }, Roles.Client));

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleSwitchReissueResult(userId, "a", "r", opaqueRole));
    }

    private sealed class StubKycSeam : IKycBffSeam
    {
        public required KycBffReviewResult ReviewOutcome { get; init; }

        public bool UpstreamEnabled => true;

        public Task<KycBffReviewResult> ReviewAsync(string submissionId, KycBffReviewInput input, CancellationToken ct)
            => Task.FromResult(ReviewOutcome);

        // Unused by these tests — minimal stubs.
        public Task<KycBffSubmitResult> SubmitByRefAsync(KycBffSubmitInput input, string idempotencyKey, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<KycBffTosStampResult> StampTosAsync(string userId, string tosAcceptedVersion, string? signatureProofRef, string idempotencyKey, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<KycBffSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
            => Task.FromResult<KycBffSubmissionView?>(null);
        public Task<KycBffQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
            => throw new NotImplementedException();
    }
}
