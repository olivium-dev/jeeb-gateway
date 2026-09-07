using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Services.Clients;
using JeebGateway.Tokens;
using JeebGateway.Users;
using JeebGateway.Users.Moderation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Um = JeebGateway.service.ServiceUserManagement;
using Xunit;

namespace JeebGateway.IntegrationTests;

// HTTP gateway + production token service, authority and both owner parsers.
// Only owner transports and token persistence are test fixtures; no live DB restart claim.
public sealed class RefreshOwnerOutageHttpTests
{
    private const string UserId = "10000000-0000-4000-8000-000000000001";
    public static IEnumerable<object[]> Cases() =>
        new[] { "/v1/auth/refresh", "/auth/refresh", "/auth/tokens/refresh", "/admin/v1/auth/refresh", "/admin/auth/refresh" }
            .SelectMany(route => new[] { "um503", "umTimeout", "ban503", "banTimeout", "banMalformed" }
                .Select(failure => new object[] { route, failure }));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OwnerOutageIs503AndSameTokenCanRetry(string route, string failure)
    {
        using var owner = new OwnerTransport();
        using var factory = Factory(owner);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var pair = await factory.Services.GetRequiredService<ITokenService>().IssueAsync(
            UserId, ["admin"], "admin", null, default);
        owner.Failure = failure;
        var unavailable = await Send(client, route, pair.RefreshToken);
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await unavailable.Content.ReadAsStringAsync();
        body.Should().NotContain("Sign in again").And.NotContain("private-owner-debug");
        unavailable.Headers.Should().NotContain(h => h.Key == "Set-Cookie",
            "a transient failure must not clear the browser refresh cookie");
        owner.Failure = "";
        var retry = await Send(client, route, pair.RefreshToken);
        retry.StatusCode.Should().Be(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
        (await Send(client, route, pair.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the successful retry really rotated the original credential");
    }

    [Theory]
    [InlineData("/admin/v1/auth/refresh")]
    [InlineData("/admin/auth/refresh")]
    public async Task SecondaryAdminOwnerReadOutageAlsoPreservesCookieAndToken(string route)
    {
        using var owner = new OwnerTransport();
        using var factory = Factory(owner);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var pair = await factory.Services.GetRequiredService<ITokenService>().IssueAsync(
            UserId, ["admin"], "admin", null, default);
        owner.Failure = "secondary503";
        var unavailable = await Send(client, route, pair.RefreshToken);
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        unavailable.Headers.Should().NotContain(h => h.Key == "Set-Cookie");
        owner.Failure = "";
        (await Send(client, route, pair.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/v1/auth/refresh", "missing")]
    [InlineData("/auth/refresh", "missing")]
    [InlineData("/auth/tokens/refresh", "missing")]
    [InlineData("/admin/v1/auth/refresh", "missing")]
    [InlineData("/admin/auth/refresh", "revoked")]
    public async Task ConfirmedInvalidIdentityStillRejectsWith401(string route, string failure)
    {
        using var owner = new OwnerTransport();
        using var factory = Factory(owner);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var pair = await factory.Services.GetRequiredService<ITokenService>().IssueAsync(
            UserId, ["admin"], "admin", null, default);
        owner.Failure = failure;
        (await Send(client, route, pair.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static WebApplicationFactory<Program> Factory(OwnerTransport owner) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:RateLimit:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRefreshRoleAuthority>();
                services.AddSingleton<IRefreshRoleAuthority, OwnerRefreshRoleAuthority>();
                services.RemoveAll<Um.ServiceUserManagementClient>();
                services.AddScoped(_ => new Um.ServiceUserManagementClient("https://um.invalid/", Client(owner, "um")));
                services.RemoveAll<IBanServiceClient>();
                services.AddScoped<IBanServiceClient>(_ => new BanServiceClient(Client(owner, "ban")));
                services.RemoveAll<IUserSuspensionSource>();
                services.AddScoped<IUserSuspensionSource, BanServiceUserSuspensionSource>();
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddScoped<IUserManagementDualRoleClient>(_ => new HttpUserManagementDualRoleClient(
                    Client(owner, "um"), NullLogger<HttpUserManagementDualRoleClient>.Instance));
            });
        });

    private static HttpClient Client(OwnerTransport owner, string host) =>
        new(owner, false) { BaseAddress = new Uri("https://" + host + ".invalid/"), Timeout = TimeSpan.FromMilliseconds(100) };

    private static Task<HttpResponseMessage> Send(HttpClient client, string route, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
            { Content = JsonContent.Create(new { refreshToken = token }) };
        if (route.StartsWith("/admin/", StringComparison.Ordinal))
        {
            request.Headers.Add("Cookie", AdminAuthController.RefreshCookie + "=" + token
                + "; " + AdminAuthController.CsrfCookie + "=csrf");
            request.Headers.Add(AdminAuthController.CsrfHeader, "csrf");
        }
        return client.SendAsync(request);
    }

    private sealed class OwnerTransport : HttpMessageHandler
    {
        public string Failure = "";
        private int _umReads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var um = request.RequestUri!.Host == "um.invalid";
            if (um) Interlocked.Increment(ref _umReads);
            if (Failure == (um ? "umTimeout" : "banTimeout"))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (Failure == (um ? "um503" : "ban503") || (um && Failure == "secondary503" && _umReads == 2))
                return Response(HttpStatusCode.ServiceUnavailable, "{\"debug\":\"private-owner-debug\"}");
            if (um && Failure == "missing")
                return Response(HttpStatusCode.NotFound, "{\"type\":\"https://docs.olivium-dev.com/errors/user-not-found\",\"status\":404}");
            if (!um && Failure == "banMalformed") return Response(HttpStatusCode.OK, "{}");
            var roles = Failure == "revoked" ? "customer" : "admin";
            var json = um
                ? System.Text.Json.JsonSerializer.Serialize(new { userId = UserId, available_roles = new[] { roles }, active_role = roles })
                : System.Text.Json.JsonSerializer.Serialize(new { user_id = UserId, ban_statuses = Array.Empty<object>() });
            return Response(HttpStatusCode.OK, json);
        }
        private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
