using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Controllers;
using JeebGateway.Financials;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Extraction adaptation of the PR #364 settlement security tests: the routes
/// are served by the in-gateway COD owner instead of the retired UPG proxy.
/// </summary>
public sealed class AdminSettlementSecurityTests
{
    [Fact]
    public void Routes_UseSeparateReadAndManageCapabilities()
    {
        Capability(nameof(AdminCodSettlementsController.Index)).Should().Be(Capabilities.AdminSettlementsRead);
        Capability(nameof(AdminCodSettlementsController.Detail)).Should().Be(Capabilities.AdminSettlementsRead);
        Capability(nameof(AdminCodSettlementsController.Batch)).Should().Be(Capabilities.AdminSettlementsRead);
        Capability(nameof(AdminCodSettlementsController.ReconcileBatch)).Should().Be(Capabilities.AdminSettlementsManage);
        Capability(nameof(AdminCodSettlementsController.Dispute)).Should().Be(Capabilities.AdminSettlementsManage);
        Capability(nameof(AdminCodSettlementsController.Resolve)).Should().Be(Capabilities.AdminSettlementsManage);
    }

    [Fact]
    public async Task MarkPaid_WithoutMfa_FailsClosedBeforeOwnerCall()
    {
        var portal = new CapturingPortal();
        var controller = Controller(portal, new[] { new Claim("sub", "finance-1") });

        var result = await controller.ReconcileBatch(
            Guid.NewGuid().ToString("D"),
            new AdminMarkSettlementPaidRequest(1, "bank-ref", "bank transfer confirmed"),
            "request-1234",
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        portal.MarkPaidCalls.Should().Be(0);
    }

    [Fact]
    public async Task MarkPaid_WithFreshMfa_ReachesOwnerWithAuditActorAndReturnsWireShape()
    {
        var portal = new CapturingPortal();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var controller = Controller(portal, new[]
        {
            new Claim("sub", "finance-1"),
            new Claim("amr", "pwd mfa"),
            new Claim("auth_time", now),
        });

        var batchId = Guid.NewGuid().ToString("D");
        var result = await controller.ReconcileBatch(
            batchId,
            new AdminMarkSettlementPaidRequest(1, "bank-ref", "bank transfer confirmed"),
            "request-1234",
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<JeebGateway.Admin.AdminSettlementReconcileResponse>();
        portal.MarkPaidCalls.Should().Be(1);
        portal.LastAdminId.Should().Be("finance-1");
        portal.LastBatchId.Should().Be(batchId);
    }

    [Fact]
    public async Task MarkPaid_Replay_EchoesIdempotencyReplayedHeader()
    {
        var portal = new CapturingPortal { ReplayNext = true };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var controller = Controller(portal, new[]
        {
            new Claim("sub", "finance-1"),
            new Claim("amr", "mfa"),
            new Claim("auth_time", now),
        });

        var result = await controller.ReconcileBatch(
            Guid.NewGuid().ToString("D"),
            new AdminMarkSettlementPaidRequest(2, "bank-ref", "already reconciled"),
            "request-1234",
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        controller.Response.Headers["Idempotency-Replayed"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task DisputeAndResolve_WithFreshMfa_FailClosedAsUnsupportedByTheCodOwner()
    {
        var portal = new CapturingPortal();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var claims = new[]
        {
            new Claim("sub", "finance-1"),
            new Claim("amr", "pwd mfa"),
            new Claim("auth_time", now),
        };

        var dispute = await Controller(portal, claims).Dispute(
            "settlement-42",
            new AdminDisputeSettlementRequest(4, "cash mismatch"),
            "dispute-request-42",
            CancellationToken.None);
        dispute.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);

        var resolve = await Controller(portal, claims).Resolve(
            "settlement-42",
            new AdminResolveSettlementRequest(5, "bank receipt verified"),
            "resolve-request-42",
            CancellationToken.None);
        resolve.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task DisputeAndResolve_WithoutMfa_RejectBeforeTheUnsupportedVerdict()
    {
        var portal = new CapturingPortal();
        var controller = Controller(portal, new[] { new Claim("sub", "finance-1") });

        var result = await controller.Dispute(
            "settlement-42",
            new AdminDisputeSettlementRequest(4, "cash mismatch"),
            "dispute-request-42",
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void RuntimeSettlementServices_ResolveTheLocalCodOwnerPortal()
    {
        using var factory = new WebApplicationFactory<Program>();
        factory.Services.GetRequiredService<ISettlementServiceClient>().Should().NotBeNull();
        factory.Services.GetRequiredService<IAdminSettlementPortalService>()
            .Should().BeOfType<AdminSettlementPortalService>();
    }

    private static string Capability(string method) =>
        typeof(AdminCodSettlementsController).GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!
            .GetCustomAttribute<RequireCapabilityAttribute>()!.Capability;

    private static AdminCodSettlementsController Controller(
        IAdminSettlementPortalService portal, IEnumerable<Claim> claims) => new(
        portal, TimeProvider.System)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                TraceIdentifier = "test-correlation",
            }
        }
    };

    private sealed class CapturingPortal : IAdminSettlementPortalService
    {
        public int MarkPaidCalls { get; private set; }
        public string? LastAdminId { get; private set; }
        public string? LastBatchId { get; private set; }
        public bool ReplayNext { get; init; }

        public Task<JeebGateway.Admin.AdminSettlementPageResponse> ListAsync(
            AdminSettlementPortalListRequest request, CancellationToken ct) =>
            Task.FromResult(new JeebGateway.Admin.AdminSettlementPageResponse(
                Array.Empty<JeebGateway.Admin.AdminSettlementResource>(),
                new JeebGateway.Admin.AdminSettlementPageCursor(null)));

        public Task<JeebGateway.Admin.AdminSettlementDetailResponse?> GetAsync(
            string settlementId, CancellationToken ct) =>
            Task.FromResult<JeebGateway.Admin.AdminSettlementDetailResponse?>(null);

        public Task<JeebGateway.Admin.AdminSettlementBatchResponse?> GetBatchAsync(
            string batchId, CancellationToken ct) =>
            Task.FromResult<JeebGateway.Admin.AdminSettlementBatchResponse?>(null);

        public Task<AdminSettlementMarkPaidResult> MarkBatchPaidAsync(
            string batchId, int expectedVersion, string paymentReference, string reason,
            string adminId, CancellationToken ct)
        {
            MarkPaidCalls++;
            LastAdminId = adminId;
            LastBatchId = batchId;
            var batch = new JeebGateway.Admin.AdminSettlementBatchResource(
                batchId, "jeeber-1", "10.00", "USD", "paid",
                DateOnly.FromDateTime(DateTime.UtcNow.Date), DateOnly.FromDateTime(DateTime.UtcNow.Date),
                1, DateTimeOffset.UtcNow, adminId, 2, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
            return Task.FromResult(new AdminSettlementMarkPaidResult(
                ReplayNext ? AdminSettlementMarkPaidOutcome.Replayed : AdminSettlementMarkPaidOutcome.Ok,
                new JeebGateway.Admin.AdminSettlementReconcileResponse(batch, ReplayNext ? 0 : 1, "not-dispatched")));
        }
    }
}
