using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServiceNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>P02 — the legacy list's blanket catch used to swallow a malformed-id contract breach and
/// truncate the page; the mapper rethrows it and the action answers the inbox's sanitized 502.</summary>
public sealed class P02LegacyNotificationListContractTests
{
    private const string GoodRowId = "notif-good-p02";

    private static string Page(string malformedEntityId) => $$"""
    {
      "total": 2,
      "items": [
        { "notification_id": "{{GoodRowId}}", "title": "ok", "notification_type": "chat",
          "entity_id": "req-1", "status": "unread", "created_at": "2026-09-05T10:00:00Z" },
        { "notification_id": "notif-bad-p02", "title": "bad", "notification_type": "chat",
          "entity_id": "{{malformedEntityId}}", "status": "unread", "created_at": "2026-09-05T10:01:00Z" }
      ]
    }
    """;

    [Theory]
    [InlineData("/")]
    [InlineData("one/two")]
    [InlineData("x?query")]
    [InlineData("..")]
    public async Task Malformed_Upstream_Id_Answers_502_Instead_Of_A_Truncated_List(string malformedEntityId)
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page(malformedEntityId), Encoding.UTF8, "application/json")
        });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Notification/messages");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "a malformed upstream id is a contract breach, not a row to drop");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("The notifications response could not be verified.");
        raw.Should().NotContain(GoodRowId,
            "answering 200 with only the rows mapped before the breach would silently truncate the page");
    }

    [Fact]
    public async Task Wellformed_Page_Still_Maps_And_Carries_The_Resolved_Deep_Link()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page("req-2"), Encoding.UTF8, "application/json")
        });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Notification/messages");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain(GoodRowId);
        raw.Should().Contain("jeeb://chat/req-1");
        raw.Should().Contain("jeeb://chat/req-2");
    }

    private static WebApplicationFactory<Program> NewFactoryWithNotificationStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceNotificationClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://notif.test/") };
                    return new ServiceNotificationClient("http://notif.test/", http);
                });
            });
        });

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", "notif-p02-legacy"), new Claim("roles", "client") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }
}
