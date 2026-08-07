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
