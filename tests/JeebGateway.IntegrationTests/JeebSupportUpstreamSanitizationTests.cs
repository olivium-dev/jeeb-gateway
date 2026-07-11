using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.JeebSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249 (info-leak, JeebSupport residual): the create/read/list store catches did
/// <c>Problem(detail: ex.Message, ...)</c> over a generic jeeb-state-service store exception,
/// which may wrap connection / driver detail — an information-disclosure leak. The fix routes
/// every store catch through <c>UpstreamProblem(Exception)</c>, which logs the exception
/// server-side ONLY and returns a generic 502. Mirrors <see cref="ChatControllerErrorShapeTests"/>.
/// </summary>
public class JeebSupportUpstreamSanitizationTests
{
    private const string Canary =
        "Npgsql.NpgsqlException: SECRET_CANARY_support42 connecting to Host=10.0.0.9;Password=hunter2";

    [Fact]
    public async Task CreateTicket_StoreFailure_Is_Sanitized_502_Not_Leaked_Message()
    {
        using var factory = NewFactoryWithThrowingStore();
        var client = MintBearerClient(factory, "support-user-jebv4-249");

        var resp = await client.PostAsync("/v1/support/tickets",
            JsonContent.Create(new { category = "order", body = "My delivery never arrived." }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "a jeeb-state-service store failure is a 502 Bad Gateway to the caller");

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Title.Should().Be("The support request could not be completed.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_support42",
            "the store exception message must never reach the client (JEBV4-249)");
        raw.Should().NotContain("Password=hunter2", "no connection-string fragment may leak");
        raw.Should().NotContain("NpgsqlException");
        raw.Should().StartWith("{", "the error body must be a JSON ProblemDetails envelope");
    }

    /// <summary>
    /// Source-scan regression guard: zero LIVE <c>detail: ex.Message</c>, and every store
    /// catch (<c>catch (Exception ex) when (ex is not OperationCanceledException)</c>) routes
    /// through <c>UpstreamProblem(ex)</c> — a 1:1 pairing (create/read/list = 3).
    /// </summary>
    [Fact]
    public void JeebSupportController_Source_All_Store_Catches_Are_Sanitized()
    {
        var path = ControllerSourceScan.Locate("JeebSupportController.cs");
        path.Should().NotBeNull();
        var liveCode = ControllerSourceScan.LiveCode(path!);

        ControllerSourceScan.Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no store catch may echo the exception message on the wire");

        var catches = ControllerSourceScan.Count(liveCode, "catch (Exception ex) when (ex is not OperationCanceledException)");
        var sanitized = ControllerSourceScan.Count(liveCode, "UpstreamProblem(ex)");
        catches.Should().BeGreaterThan(0, "the guard must actually see the store catch sites");
        sanitized.Should().Be(catches,
            "every store catch must return UpstreamProblem(ex) — a single-site revert breaks this pairing");
    }

    private static WebApplicationFactory<Program> NewFactoryWithThrowingStore()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJeebSupportTicketStore>();
                services.AddScoped<IJeebSupportTicketStore>(_ => new ThrowingSupportStore(Canary));
            });
        });

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory, string sub)
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
            claims: new[] { new Claim("sub", sub), new Claim("roles", "client") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class ThrowingSupportStore : IJeebSupportTicketStore
    {
        private readonly string _canary;
        public ThrowingSupportStore(string canary) => _canary = canary;

        public Task<SupportTicketRow> CreateAsync(SupportTicketRow row, CancellationToken ct)
            => throw new InvalidOperationException(_canary);

        public Task<SupportTicketRow?> GetAsync(string id, CancellationToken ct)
            => throw new InvalidOperationException(_canary);

        public Task<IReadOnlyList<SupportTicketRow>> ListByOwnerAsync(string ownerId, CancellationToken ct)
            => throw new InvalidOperationException(_canary);
    }
}
