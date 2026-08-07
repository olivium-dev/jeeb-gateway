using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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

public sealed class AdminSettlementSecurityTests
{
    [Fact]
    public void Routes_UseSeparateReadAndManageCapabilities()
    {
        Capability(nameof(AdminSettlementsController.Index)).Should().Be(Capabilities.AdminSettlementsRead);
        Capability(nameof(AdminSettlementsController.Detail)).Should().Be(Capabilities.AdminSettlementsRead);
        Capability(nameof(AdminSettlementsController.ReconcileBatch)).Should().Be(Capabilities.AdminSettlementsManage);
        Capability(nameof(AdminSettlementsController.Dispute)).Should().Be(Capabilities.AdminSettlementsManage);
        Capability(nameof(AdminSettlementsController.Resolve)).Should().Be(Capabilities.AdminSettlementsManage);
    }

    [Fact]
    public async Task MarkPaid_WithoutMfa_FailsClosedBeforeOwnerCall()
    {
        var handler = new CapturingHandler();
        var controller = Controller(handler, new[] { new Claim("sub", "finance-1") });

        var result = await controller.ReconcileBatch(
            "batch-1",
            new AdminMarkSettlementPaidRequest(1, "bank-ref", "bank transfer confirmed"),
            "request-1234",
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task MarkPaid_WithFreshMfa_ForwardsAuditHeadersWithoutAnyAuthorizationCredential()
    {
        var handler = new CapturingHandler();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var controller = Controller(handler, new[]
        {
            new Claim("sub", "finance-1"),
            new Claim("amr", "pwd mfa"),
            new Claim("auth_time", now),
        });

        var result = await controller.ReconcileBatch(
            "batch-1",
            new AdminMarkSettlementPaidRequest(1, "bank-ref", "bank transfer confirmed"),
            "request-1234",
            CancellationToken.None);

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(StatusCodes.Status200OK);
        handler.Request.Should().NotBeNull();
        handler.Request!.Headers.Authorization.Should().BeNull();
        handler.Request.Headers.Contains("X-Api-Key").Should().BeFalse();
        handler.Request.Headers.GetValues("X-Admin-Id").Single().Should().Be("finance-1");
        handler.Request.Headers.GetValues("Idempotency-Key").Single().Should().Be("request-1234");
    }

    [Fact]
    public async Task DisputeAndResolve_WithFreshMfa_ForwardTypedVersionedMutationsWithoutCredentials()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var claims = new[]
        {
            new Claim("sub", "finance-1"),
            new Claim("amr", "pwd mfa"),
            new Claim("auth_time", now),
        };

        var disputeHandler = new CapturingHandler();
        var dispute = await Controller(disputeHandler, claims).Dispute(
            "settlement/42",
            new AdminDisputeSettlementRequest(4, "cash mismatch"),
            "dispute-request-42",
            CancellationToken.None);

        dispute.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(StatusCodes.Status200OK);
        disputeHandler.Request!.Method.Should().Be(HttpMethod.Post);
        disputeHandler.Request.RequestUri!.PathAndQuery.Should()
            .Be("/admin/v1/settlements/settlement%2F42/dispute");
        disputeHandler.Request.Headers.Authorization.Should().BeNull();
        disputeHandler.Request.Headers.Contains("X-Api-Key").Should().BeFalse();
        disputeHandler.Request.Headers.GetValues("Idempotency-Key").Should().Equal("dispute-request-42");
        (await disputeHandler.Request.Content!.ReadAsStringAsync()).Should()
            .Contain("\"expectedVersion\":4").And.Contain("\"reason\":\"cash mismatch\"");

        var resolveHandler = new CapturingHandler();
        var resolve = await Controller(resolveHandler, claims).Resolve(
            "settlement-42",
            new AdminResolveSettlementRequest(5, "bank receipt verified"),
            "resolve-request-42",
            CancellationToken.None);

        resolve.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(StatusCodes.Status200OK);
        resolveHandler.Request!.RequestUri!.PathAndQuery.Should()
            .Be("/admin/v1/settlements/settlement-42/resolve");
        (await resolveHandler.Request.Content!.ReadAsStringAsync()).Should()
            .Contain("\"expectedVersion\":5").And.Contain("\"resolutionNote\":\"bank receipt verified\"");
    }

    [Theory]
    [InlineData(500, "database password leaked")]
    [InlineData(200, "<html>owner crash</html>")]
    public async Task OwnerErrorsAndMalformedBodiesAreReplacedWithSanitizedGatewayProblems(
        int ownerStatus,
        string ownerBody)
    {
        var handler = new CapturingHandler((HttpStatusCode)ownerStatus, ownerBody);
        var controller = Controller(handler, new[] { new Claim("sub", "finance-1") });

        var result = await controller.Detail("settlement-42", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        problem.Value.Should().BeOfType<ProblemDetails>();
        System.Text.Json.JsonSerializer.Serialize(problem.Value).Should().NotContain(ownerBody);
    }

    [Fact]
    public void RuntimeSettlementServices_AreOwnerBackedAndHaveNoGatewayBatchProcessor()
    {
        using var factory = new WebApplicationFactory<Program>();
        factory.Services.GetRequiredService<ISettlementStore>().Should().BeOfType<UpgSettlementStore>();
        factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().NotContain(service => service.GetType().Namespace == typeof(ISettlementStore).Namespace
                                           && service.GetType().Name.Contains("Settlement", StringComparison.Ordinal));
    }

    private static string Capability(string method) =>
        typeof(AdminSettlementsController).GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!
            .GetCustomAttribute<RequireCapabilityAttribute>()!.Capability;

    private static AdminSettlementsController Controller(
        CapturingHandler handler, IEnumerable<Claim> claims)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://upg.internal/") };
        var controller = new AdminSettlementsController(new SingleClientFactory(http), TimeProvider.System)
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
        return controller;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(
            HttpStatusCode status = HttpStatusCode.OK,
            string body = "{\"data\":{\"status\":\"paid\"}}")
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                Request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
                Request.Content = new StringContent(await request.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json");

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
