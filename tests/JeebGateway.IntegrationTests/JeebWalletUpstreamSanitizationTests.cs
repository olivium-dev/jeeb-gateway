using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249 (info-leak, JeebWallet residual): the pre-fix
/// <c>JeebWalletController.UpstreamProblem</c> helper returned
/// <c>Problem(detail: ex.Message, ...)</c>. The NSwag <see cref="ApiException"/>.Message
/// wraps the raw upstream wallet-service response body, so the balance-read catch leaked
/// upstream exception detail to the caller. The fix routes the GENERAL catch through the
/// single-arg <c>UpstreamProblem(WalletApiException)</c>, which preserves/clamps the status
/// but logs the upstream detail server-side ONLY. The deliberate <c>when (404) → Ok(empty)</c>
/// graceful-degrade branch is unchanged. Mirrors <see cref="ChatControllerErrorShapeTests"/>.
/// </summary>
public class JeebWalletUpstreamSanitizationTests
{
    // A GUID user id is required: TryResolveHolderId parses the caller id as a wallet-holder GUID.
    private const string HolderGuid = "11111111-1111-1111-1111-111111111111";

    // The kind of internal exception text the upstream must never leak to a client.
    private const string Canary =
        "System.NullReferenceException: SECRET_CANARY_wallet7f3 at WalletService.Internal.SecretRepo.Load() line 42";

    [Fact]
    public async Task GetBalance_UpstreamServerError_Is_Sanitized_ProblemDetails_Not_Leaked_Message()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithWalletStub(stub);
        var client = MintBearerClient(factory, HolderGuid);

        var resp = await client.GetAsync("/v1/jeeb/wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("The wallet request could not be completed.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_wallet7f3",
            "the upstream response body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("NullReferenceException");
        raw.Should().NotContain("The HTTP status code of the response was not expected",
            "the NSwag ApiException.Message wrapper must not be echoed either");
        raw.Should().StartWith("{", "the error body must be a JSON ProblemDetails envelope, not a bare string");
    }

    [Fact]
    public async Task GetBalance_Upstream404_Degrades_To_Empty_Wallet_Without_Leaking_Body()
    {
        // A 404 from wallet-service means "no holder provisioned yet" → an empty wallet, not
        // an error. This graceful branch must stay a 200 and must not leak the upstream body.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithWalletStub(stub);
        var client = MintBearerClient(factory, HolderGuid);

        var resp = await client.GetAsync("/v1/jeeb/wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an upstream 404 is the empty-wallet default, not a client-facing error");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_wallet7f3");
    }

    [Fact]
    public async Task GetBalance_Upstream_Status_Outside_Error_Range_Is_Clamped_To_502()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) // 302 — outside [400,600)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithWalletStub(stub);
        var client = MintBearerClient(factory, HolderGuid);

        var resp = await client.GetAsync("/v1/jeeb/wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "an upstream status outside [400,600) must be clamped to 502, never forwarded");
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Title.Should().Be("The wallet request could not be completed.");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_wallet7f3");
    }

    /// <summary>
    /// Source-scan regression guard (same grep-guard idiom as ChatControllerErrorShapeTests):
    /// pins zero LIVE occurrences of the <c>detail: ex.Message</c> leak and that the GENERAL
    /// wallet catch routes through <c>UpstreamProblem(ex)</c>. The <c>when (404)</c> graceful
    /// catch intentionally returns <c>Ok(empty)</c> and is NOT counted.
    /// </summary>
    [Fact]
    public void JeebWalletController_Source_Has_No_Live_Upstream_Detail_Leak()
    {
        var path = ControllerSourceScan.Locate("JeebWalletController.cs");
        path.Should().NotBeNull("src/JeebGateway/Controllers/JeebWalletController.cs must be locatable");

        var liveCode = ControllerSourceScan.LiveCode(path!);

        ControllerSourceScan.Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no catch may echo the upstream ex.Message/body on the wire");

        var catches = ControllerSourceScan.Count(liveCode, "catch (WalletApiException");
        catches.Should().BeGreaterThan(0, "the guard must actually see the upstream catch sites");

        // 1 general catch routed through the helper; the other WalletApiException catch is the
        // deliberate `when (404) → Ok(empty)` graceful-degrade path (not a leak, not routed).
        ControllerSourceScan.Count(liveCode, "UpstreamProblem(ex)").Should().Be(1,
            "the general wallet catch must route through the sanitizing UpstreamProblem(ex) helper");
        ControllerSourceScan.Count(liveCode, "when (ex.StatusCode == StatusCodes.Status404NotFound)")
            .Should().Be(1, "the graceful empty-wallet 404 branch must remain");
    }

    private static WebApplicationFactory<Program> NewFactoryWithWalletStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceWalletClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://wallet.test/") };
                    return new ServiceWalletClient("http://wallet.test/", http);
                });
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
            claims: new[] { new Claim("sub", sub), new Claim("roles", "jeeber") },
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
