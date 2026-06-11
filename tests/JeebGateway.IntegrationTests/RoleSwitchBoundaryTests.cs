using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1481 (JEB-39 / T-BE-003) — gateway-side boundary invariants for the role-switch path.
///
/// C1/GR2: the Jeeb CONTRACT role vocabulary ({client, jeeber}) lives ONLY in the gateway
///   (<see cref="JeebRoleTranslator"/>). An unknown role is rejected LOCALLY (invalid_role 400)
///   before any user-management call — UM never sees the product whitelist.
/// C2/N11: on a role switch the gateway is a thin relay — user-management persists active_role
///   and RE-ISSUES the token pair; the gateway forwards UM's tokens VERBATIM and signs nothing.
/// C3/N5: user-management's distinct 403 role_not_available is mapped straight through
///   (<see cref="UserManagementRoleNotAvailableException"/>), separate from the gateway-local
///   400 invalid_role; any other non-success is a <see cref="UserManagementCallException"/>.
/// </summary>
public sealed class RoleSwitchBoundaryTests
{
    // ---- C1: contract vocabulary + invalid_role gate live ONLY in the gateway ----

    [Theory]
    [InlineData("client", Roles.Client)]   // contract -> opaque
    [InlineData("jeeber", Roles.Jeeber)]
    [InlineData("CLIENT", Roles.Client)]   // case-insensitive
    [InlineData("Jeeber", Roles.Jeeber)]
    public void ToOpaque_MapsContractRoles(string contract, string expectedOpaque)
    {
        JeebRoleTranslator.ToOpaque(contract).Should().Be(expectedOpaque);
        JeebRoleTranslator.IsContractRole(contract).Should().BeTrue();
    }

    [Theory]
    [InlineData("admin")]      // not a Jeeb contract role
    [InlineData("manager")]
    [InlineData("customer")]   // the OPAQUE value is not a valid INBOUND contract role
    [InlineData("driver")]
    [InlineData("")]
    [InlineData(null)]
    public void ToOpaque_ReturnsNull_ForUnknownRole_SoGatewayFails400_BeforeAnyUmCall(string? unknown)
    {
        // null => caller maps to 400 invalid_role WITHOUT calling user-management (N6).
        JeebRoleTranslator.ToOpaque(unknown).Should().BeNull();
        JeebRoleTranslator.IsContractRole(unknown).Should().BeFalse();
    }

    // ---- C2 / C3: the UM relay maps 403 distinctly and forwards tokens verbatim ----

    [Fact]
    public async Task RoleSwitch_Relays_UM_Tokens_Verbatim_GatewaySignsNothing()
    {
        // C2 — UM is the token authority: it re-issues the pair, the gateway relays it.
        var handler = new StubHandler(HttpStatusCode.OK,
            """{ "userId":"u-1", "accessToken":"UM-ACCESS", "refreshToken":"UM-REFRESH", "activeRole":"driver" }""");
        var client = NewClient(handler);

        var result = await client.RoleSwitchAsync("u-1", Roles.Jeeber, CancellationToken.None);

        // Verbatim relay: the gateway did NOT re-sign — the UM-issued strings pass straight through.
        result.AccessToken.Should().Be("UM-ACCESS");
        result.RefreshToken.Should().Be("UM-REFRESH");
        result.ActiveRole.Should().Be("driver");
        handler.LastRequestPath.Should().Be("/api/User/role/switch");
    }

    [Fact]
    public async Task RoleSwitch_Maps_UM_403_To_RoleNotAvailable()
    {
        // C3 — UM's distinct 403 role_not_available is mapped straight through (N5), separate
        // from the gateway-local 400 invalid_role.
        var handler = new StubHandler(HttpStatusCode.Forbidden,
            """{ "type":"https://docs.olivium-dev.com/errors/role-not-available" }""");
        var client = NewClient(handler);

        var act = async () => await client.RoleSwitchAsync("u-1", Roles.Jeeber, CancellationToken.None);

        await act.Should().ThrowAsync<UserManagementRoleNotAvailableException>();
    }

    [Fact]
    public async Task RoleSwitch_Maps_Other_NonSuccess_To_CallException()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{}");
        var client = NewClient(handler);

        var act = async () => await client.RoleSwitchAsync("u-1", Roles.Jeeber, CancellationToken.None);

        (await act.Should().ThrowAsync<UserManagementCallException>())
            .Which.StatusCode.Should().Be(500);
    }

    // ---- harness ----

    private static HttpUserManagementDualRoleClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://um.local/") };
        return new HttpUserManagementDualRoleClient(
            http, NullLogger<HttpUserManagementDualRoleClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? LastRequestPath { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
