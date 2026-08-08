using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// F3 (unregister-as-jeeber) — <see cref="HttpUserManagementDualRoleClient.RemoveAvailableRoleAsync"/>
/// boundary tests, mirroring <see cref="RoleSwitchBoundaryTests"/>'s pattern for the sibling
/// grant/switch seams. Plan correction 9: UM has no revoke op today, so the live-shape test
/// pins "any non-2xx becomes UserManagementCallException", including the 404 a real call gets.
/// </summary>
public sealed class UnregisterJeeberSeamTests
{
    [Fact]
    public async Task RemoveAvailableRole_Posts_ToRoleRevokeRoute_WithOpaqueRole()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{ "userId":"u-1", "available_roles":["customer"], "added":true }""");
        var client = NewClient(handler);

        var result = await client.RemoveAvailableRoleAsync("u-1", Roles.Jeeber, CancellationToken.None);

        handler.LastRequestPath.Should().Be("/api/User/role/revoke");
        result.UserId.Should().Be("u-1");
        result.AvailableRoles.Should().BeEquivalentTo(new[] { "customer" });
        result.Added.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAvailableRole_UM_404_TodaysLiveShape_MapsTo_CallException()
    {
        // Correction 9 — UM has no revoke op; a live call 404s. The controller maps this
        // to 502 upstream_fault rather than fabricating success.
        var handler = new StubHandler(HttpStatusCode.NotFound, "{}");
        var client = NewClient(handler);

        var act = async () => await client.RemoveAvailableRoleAsync("u-1", Roles.Jeeber, CancellationToken.None);

        (await act.Should().ThrowAsync<UserManagementCallException>()).Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task RemoveAvailableRole_Maps_Other_NonSuccess_To_CallException()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{}");
        var client = NewClient(handler);

        var act = async () => await client.RemoveAvailableRoleAsync("u-1", Roles.Jeeber, CancellationToken.None);

        (await act.Should().ThrowAsync<UserManagementCallException>()).Which.StatusCode.Should().Be(500);
    }

    // ---- harness (mirrors RoleSwitchBoundaryTests) ----

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
