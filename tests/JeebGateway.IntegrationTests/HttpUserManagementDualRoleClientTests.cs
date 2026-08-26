using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Contract tests for the concrete user-management HTTP adapter. Every request is handled by
/// an in-process <see cref="HttpMessageHandler"/>; these tests open no socket and contact no
/// live service.
/// </summary>
public sealed class HttpUserManagementDualRoleClientTests
{
    private const string UserId = "41a864a2-42c6-4e0c-8ecb-0878df34ff07";
    private const string OtherUserId = "9162d99a-10d2-40ab-9027-b6a6cb82647e";
    private const string Phone = "+9613000199";

    [Fact]
    public async Task FreshIdentityOnlyCreate_FollowedByAuthoritativeRolesRead_ParsesSameCanonicalCustomerIdentity()
    {
        var call = 0;
        var handler = new ControlledHandler(async (request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                request.Method.Should().Be(HttpMethod.Post);
                request.RequestUri!.AbsolutePath.Should().Be("/api/users/phone-identity/find-or-create");

                using var requestBody = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                requestBody.RootElement.EnumerateObject().Select(property => property.Name).Should()
                    .Equal(new[] { "phone" },
                        "the identity surface sends no caller-local role fields");
                requestBody.RootElement.GetProperty("phone").GetString().Should().Be(Phone);

                return JsonResponse(HttpStatusCode.OK,
                    $$"""{ "userId": "{{UserId}}", "isNew": true, "phone": "{{Phone}}" }""");
            }

            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be($"/api/User/{UserId}/roles");
            return JsonResponse(HttpStatusCode.OK,
                $$"""{ "userId": "{{UserId}}", "available_roles": ["customer"], "active_role": "customer" }""");
        });
        using var http = MakeHttpClient(handler);
        var client = MakeClient(http);

        var identity = await client.PhoneFindOrCreateAsync(Phone, CancellationToken.None);
        var roles = await client.GetUserRolesAsync(identity.UserId, CancellationToken.None);

        identity.UserId.Should().Be(UserId);
        identity.IsNew.Should().BeTrue();
        roles.Should().NotBeNull();
        roles!.UserId.Should().Be(UserId);
        roles.AvailableRoles.Should().Equal(Roles.Client);
        roles.ActiveRole.Should().Be(Roles.Client);
        call.Should().Be(2, "OTP authority requires the separate roles GET after identity resolution");
    }

    [Fact]
    public async Task GetRoles_WhenAuthorityReturns404_ReturnsAbsentAuthority()
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var client = MakeClient(http);

        var roles = await client.GetUserRolesAsync(UserId, CancellationToken.None);

        roles.Should().BeNull();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task GetRoles_WhenAuthorityReturns5xx_ReturnsAbsentAuthority(int status)
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var client = MakeClient(http);

        var roles = await client.GetUserRolesAsync(UserId, CancellationToken.None);

        roles.Should().BeNull();
    }

    [Fact]
    public async Task GetRoles_WhenAuthorityReturnsJsonNull_ReturnsAbsentAuthority()
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "null"))));
        var client = MakeClient(http);

        var roles = await client.GetUserRolesAsync(UserId, CancellationToken.None);

        roles.Should().BeNull();
    }

    [Fact]
    public async Task GetRoles_WhenAuthorityReturnsMalformedJson_PropagatesParsingFault()
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "{not-json"))));
        var client = MakeClient(http);

        Func<Task> act = async () =>
            await client.GetUserRolesAsync(UserId, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task GetRoles_WhenAuthorityOmitsRequiredFields_PreservesInvalidDataForCallerToReject()
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))));
        var client = MakeClient(http);

        var roles = await client.GetUserRolesAsync(UserId, CancellationToken.None);

        roles.Should().NotBeNull();
        roles!.UserId.Should().BeEmpty();
        roles.AvailableRoles.Should().BeEmpty();
        roles.ActiveRole.Should().BeNull();
    }

    [Fact]
    public async Task GetRoles_WhenAuthorityReturnsDifferentCanonicalIdentity_PreservesMismatchForCallerToReject()
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK,
                $$"""{ "userId": "{{OtherUserId}}", "available_roles": ["customer"], "active_role": "customer" }"""))));
        var client = MakeClient(http);

        var roles = await client.GetUserRolesAsync(UserId, CancellationToken.None);

        roles.Should().NotBeNull();
        roles!.UserId.Should().Be(OtherUserId);
        roles.AvailableRoles.Should().Equal(Roles.Client);
        roles.ActiveRole.Should().Be(Roles.Client);
    }

    [Fact]
    public async Task GetRoles_WhenDependencyTimesOut_PropagatesDependencyCancellation()
    {
        using var http = MakeHttpClient(new ControlledHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("dependency timeout"))));
        var client = MakeClient(http);

        Func<Task> act = async () =>
            await client.GetUserRolesAsync(UserId, CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task GetRoles_WhenCallerCancels_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = MakeHttpClient(new ControlledHandler((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var client = MakeClient(http);

        Func<Task> act = async () =>
            await client.GetUserRolesAsync(UserId, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    private static HttpUserManagementDualRoleClient MakeClient(HttpClient http) =>
        new(http, NullLogger<HttpUserManagementDualRoleClient>.Instance);

    private static HttpClient MakeHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://user-management.invalid/"),
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class ControlledHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
