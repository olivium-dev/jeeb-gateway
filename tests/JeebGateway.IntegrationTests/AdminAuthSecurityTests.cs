using System.Net;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class AdminAuthSecurityTests
{
    [Fact]
    public async Task PasswordLoginRouteIsNotPublished()
    {
        using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().PostAsync(
            "/admin/v1/auth/login", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the fallback authorization policy challenges unknown unauthenticated routes; "
            + "the OpenAPI contract separately proves the password action is absent");
    }

    [Theory]
    [InlineData("/admin/v1/auth/refresh")]
    [InlineData("/admin/v1/auth/logout")]
    public async Task CookieMutation_WithoutDoubleSubmitCsrf_IsForbidden(string route)
    {
        using var factory = new WebApplicationFactory<Program>();
        var response = await factory.CreateClient().PostAsync(route, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task LogoutClearsBrowserCookiesEvenWhenServerRevocationFails()
    {
        var controller = new AdminAuthController(
            null!, null!, new ThrowingTokenService(),
            new TestingEnvironment(), new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.Request.Headers.Cookie =
            $"{AdminAuthController.RefreshCookie}=refresh; {AdminAuthController.CsrfCookie}=csrf";
        controller.Request.Headers[AdminAuthController.CsrfHeader] = "csrf";

        Func<Task> logout = async () => await controller.Logout(CancellationToken.None);

        await logout.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("revocation unavailable");
        var cookies = controller.Response.Headers.SetCookie.ToString();
        cookies.Should().Contain(AdminAuthController.RefreshCookie + "=");
        cookies.Should().Contain(AdminAuthController.CsrfCookie + "=");
    }

    // Staging terminates TLS at nginx and the Swarm ingress peer is not a trusted
    // proxy, so Request.Scheme is http while the browser-visible scheme is https.
    [Theory]
    [InlineData("http://app.jeeb.fds-1.com", "origin_rejected")]
    [InlineData("https://app.jeeb.fds-1.com", "csrf_rejected")]
    public async Task Refresh_ComparesOriginAgainstConfiguredPublicScheme(
        string suppliedOrigin, string expectedProblem)
    {
        var controller = EdgeController(
            requestScheme: "http",
            host: "app.jeeb.fds-1.com",
            settings: new Dictionary<string, string?>
            {
                ["Gateway:PublicBaseUrl"] = "https://app.jeeb.fds-1.com",
                ["AdminPortal:AllowedOrigins:0"] = "https://cms.jeeb.fds-1.com",
            });
        controller.Request.Headers.Origin = suppliedOrigin;
        controller.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        var result = (ObjectResult)await controller.Refresh(CancellationToken.None);

        result.StatusCode.Should().Be(403);
        ((ProblemDetails)result.Value!).Type.Should()
            .Be($"https://jeeb.dev/errors/{expectedProblem}");
    }

    [Fact]
    public async Task Refresh_KeepsPlainHttpSameOriginOnAnUnterminatedHost()
    {
        var controller = EdgeController(
            requestScheme: "http",
            host: "192.168.2.39:10090",
            settings: new Dictionary<string, string?>
            {
                ["Gateway:PublicBaseUrl"] = "http://192.168.2.39:10090",
            });
        controller.Request.Headers.Origin = "http://192.168.2.39:10090";
        controller.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        var result = (ObjectResult)await controller.Refresh(CancellationToken.None);

        result.StatusCode.Should().Be(403);
        ((ProblemDetails)result.Value!).Type.Should()
            .Be("https://jeeb.dev/errors/csrf_rejected");
    }

    private static AdminAuthController EdgeController(
        string requestScheme, string host, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings).Build();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = requestScheme;
        httpContext.Request.Host = new HostString(host);
        httpContext.Request.Path = "/admin/v1/auth/refresh";
        // The edge does send X-Forwarded-Proto; it is dropped as untrusted.
        httpContext.Request.Headers["X-Forwarded-Proto"] = "https";
        return new AdminAuthController(
            null!, null!, new ThrowingTokenService(),
            new TestingEnvironment { EnvironmentName = "Production" }, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private sealed class ThrowingTokenService : ITokenService
    {
        public Task<TokenPair> IssueAsync(
            string userId, IEnumerable<string> roles, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RevokeAsync(
            string refreshToken, RevocationReason reason, CancellationToken ct) =>
            throw new InvalidOperationException("revocation unavailable");

        public Task<int> RevokeAllForUserAsync(
            string userId, RevocationReason reason, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TestingEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "JeebGateway.IntegrationTests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
