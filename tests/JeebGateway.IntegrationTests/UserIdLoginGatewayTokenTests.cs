using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmLoginRequest = JeebGateway.service.ServiceUserManagement.UserIdLoginRequest;
using UmLoginResponse = JeebGateway.service.ServiceUserManagement.SocialLoginResponse;

namespace JeebGateway.IntegrationTests;

public sealed class UserIdLoginGatewayTokenTests
{
    private const string UserId = "super-login-gateway-audience-user";

    [Fact]
    public async Task UserIdLogin_Replaces_Upstream_Tokens_With_Gateway_Audience_Session()
    {
        var roleOwner = new TestUserManagementDualRoleClient();
        roleOwner.Seed(
            UserId,
            new[] { Roles.Client, Roles.Jeeber },
            Roles.Jeeber);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(new SuccessfulLoginClient());
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(roleOwner);
            }));

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login",
            new { userId = UserId, superAdminPassCode = "test-passcode" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("authToken").GetString();
        var refreshToken = body.GetProperty("refreshToken").GetString();
        accessToken.Should().NotBe(SuccessfulLoginClient.UpstreamAccessToken);
        refreshToken.Should().NotBe(SuccessfulLoginClient.UpstreamRefreshToken);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        jwt.Issuer.Should().Be("jeeb-gateway");
        jwt.Audiences.Should().Contain("jeeb-clients");
        jwt.Subject.Should().Be(UserId);
        jwt.Claims.Where(claim => claim.Type == "roles").Select(claim => claim.Value)
            .Should().BeEquivalentTo(new[] { Roles.Client, Roles.Jeeber });
        jwt.Claims.Single(claim => claim.Type == "active_role").Value
            .Should().Be(Roles.Jeeber);
    }

    private sealed class SuccessfulLoginClient : UmClient
    {
        internal const string UpstreamAccessToken = "upstream-user-management-access";
        internal const string UpstreamRefreshToken = "upstream-user-management-refresh";

        internal SuccessfulLoginClient() : base("http://user-management.test", new HttpClient())
        {
        }

        public override Task<UmLoginResponse> UserIdLoginAsync(
            UmLoginRequest? body,
            CancellationToken cancellationToken)
        {
            body.Should().NotBeNull();
            body!.UserId.Should().Be(UserId);
            body.SuperAdminPassCode.Should().Be("test-passcode");
            return Task.FromResult(new UmLoginResponse
            {
                UserId = UserId,
                AuthToken = UpstreamAccessToken,
                RefreshToken = UpstreamRefreshToken,
                RecentlyCreated = false,
            });
        }
    }
}
