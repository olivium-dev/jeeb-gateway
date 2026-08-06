using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Cases;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249: generic case upstream failures must remain private at the support API boundary.
/// </summary>
public class JeebSupportUpstreamSanitizationTests
{
    private const string Canary =
        "Npgsql.NpgsqlException: SECRET_CANARY_support42 connecting to Host=10.0.0.9;Password=hunter2";

    [Fact]
    public async Task CreateTicket_StateFailure_Is_Sanitized_502_Not_Leaked_Message()
    {
        using var factory = NewFactoryWithThrowingCaseService();
        var client = MintBearerClient(factory, "support-user-jebv4-249");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "support-create-sanitized");

        var resp = await client.PostAsync("/v1/support/tickets",
            JsonContent.Create(new { category = "order", body = "My delivery never arrived." }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "a jeeb-state-service case failure is a 502 Bad Gateway to the caller");

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Detail.Should().Be("The case service could not complete the request.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_support42",
            "the upstream response body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("Password=hunter2", "no connection-string fragment may leak");
        raw.Should().NotContain("NpgsqlException");
        raw.Should().StartWith("{", "the error body must be a JSON ProblemDetails envelope");
    }

    [Fact]
    public async Task CreateTicket_Requires_Caller_Idempotency_Key()
    {
        using var factory = NewFactoryWithThrowingCaseService();
        var client = MintBearerClient(factory, "support-user-idempotency");

        var response = await client.PostAsync("/v1/support/tickets",
            JsonContent.Create(new { category = "order", body = "Missing my parcel." }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Idempotency-Key is required");
    }

    /// <summary>
    /// Source-scan regression guard for all support case endpoints and the shared error mapper.
    /// </summary>
    [Fact]
    public void JeebSupportController_Source_All_Case_Catches_Are_Sanitized()
    {
        var controllerPath = ControllerSourceScan.Locate("JeebSupportController.cs");
        var basePath = ControllerSourceScan.Locate("CaseControllerBase.cs");
        controllerPath.Should().NotBeNull();
        basePath.Should().NotBeNull();
        var controllerCode = ControllerSourceScan.LiveCode(controllerPath!);
        var baseCode = ControllerSourceScan.LiveCode(basePath!);

        controllerCode.Should().NotContain("error.Message");
        baseCode.Should().NotContain("ResponseBody");

        var catches = ControllerSourceScan.Count(controllerCode,
            "catch (Exception error) when (error is not OperationCanceledException)");
        var sanitized = ControllerSourceScan.Count(controllerCode, "CaseProblem(error,");
        catches.Should().BeGreaterThan(0, "the guard must actually see the case catch sites");
        sanitized.Should().Be(catches,
            "every support case catch must use the shared sanitized mapper");
        baseCode.Should().Contain("The case service could not complete the request.");
    }

    private static WebApplicationFactory<Program> NewFactoryWithThrowingCaseService()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGenericCaseGatewayService>();
                services.AddScoped<IGenericCaseGatewayService>(_ => new ThrowingCaseService(Canary));
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

    private sealed class ThrowingCaseService(string canary) : IGenericCaseGatewayService
    {
        public Task<GenericCaseDetailV1> CreateSupportAsync(CreateSupportCaseInput input, CancellationToken ct)
            => throw new GenericCaseApiException(502, canary);

        public Task<GenericCaseDetailV1> CreateDisputeAsync(CreateDisputeCaseInput input, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GenericCaseDetailV1> GetForUserAsync(string caseId, string userId, bool isAdmin, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GenericCasePageV1> ListForUserAsync(string kind, string userId, GenericCaseQueryV1 query, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GenericCasePageV1> ListAdminAsync(GenericCaseQueryV1 query, bool? unassigned, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GenericCaseDetailV1> PatchAsync(string caseId, PatchGenericCaseRequestV1 patch,
            string actorId, string actorRole, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GenericCaseDetailV1> AddMessageAsync(string caseId, int expectedVersion, string messageType,
            string actorId, string actorRole, string idempotencyKey, string? body, Guid? replyToId,
            IReadOnlyList<string>? attachments, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<GenericCaseDetailV1> ReopenAsync(string caseId, int expectedVersion, string actorId,
            string actorRole, string idempotencyKey, string? reason, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
