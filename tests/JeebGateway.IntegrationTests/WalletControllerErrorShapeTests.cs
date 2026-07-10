using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-253 (info-leak): WalletController's 8 upstream catches were the JEBV4-63
/// "detail-field" leak shape —
/// <c>Problem(statusCode: ex.StatusCode, detail: ex.Message, title: ...)</c>. The
/// NSwag <see cref="ApiException"/>.Message embeds up to 512 chars of the raw
/// upstream wallet-service response body, so the <c>detail</c> field leaked upstream
/// exception text to the caller inside an otherwise-well-formed ProblemDetails
/// envelope. The fix routes every catch through
/// <c>WalletController.UpstreamProblem</c>, which DROPS the detail (logged
/// server-side only) while preserving the status and the generic title.
///
/// <para>Complements <see cref="ErrorShapeNormalizationTests"/> (which pins the
/// status + title for a 502 relay) by adding the leak-absence + status-clamp locks
/// and the source-scan guard. The admin-gated <c>/api/Wallet/system-wallet</c> read
/// exercises the shared <c>catch (WalletApiException) → UpstreamProblem</c> path via
/// a raw HttpClient stub bound directly to the scoped
/// <see cref="ServiceWalletClient"/>.</para>
/// </summary>
public class WalletControllerErrorShapeTests
{
    private const string Canary =
        "System.NullReferenceException: SECRET_CANARY_w253 at WalletService.Internal.SecretRepo.Load() line 42";

    [Fact]
    public async Task GetSystemWallet_UpstreamServerError_Drops_Detail_Keeps_ProblemDetails_Envelope()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithWalletStub(stub);
        var client = AdminClient(factory);

        var resp = await client.GetAsync("/api/Wallet/system-wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("Upstream wallet-service error");
        problem.Detail.Should().BeNull(
            "the upstream ex.Message (which embeds the raw upstream body) must no longer be echoed in the detail field (JEBV4-253)");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_w253",
            "the upstream response body must never reach the client");
        raw.Should().NotContain("NullReferenceException");
        raw.Should().NotContain("The HTTP status code of the response was not expected",
            "the NSwag ApiException.Message wrapper must not be echoed either");
        raw.Should().StartWith("{",
            "the error body must be a JSON ProblemDetails envelope");
    }

    [Fact]
    public async Task GetSystemWallet_UpstreamNotFound_Preserves_Status_And_Does_Not_Leak_Body()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithWalletStub(stub);
        var client = AdminClient(factory);

        var resp = await client.GetAsync("/api/Wallet/system-wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.NotFound);
        problem.Title.Should().Be("Upstream wallet-service error");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_w253",
            "the upstream response body must never reach the client, even on a 404");
    }

    [Fact]
    public async Task GetSystemWallet_Upstream_Status_Outside_Error_Range_Is_Clamped_To_502()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) // 302
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithWalletStub(stub);
        var client = AdminClient(factory);

        var resp = await client.GetAsync("/api/Wallet/system-wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "an upstream status outside [400,600) must be clamped to 502, never forwarded");
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Title.Should().Be("Upstream wallet-service error");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_w253");
    }

    /// <summary>
    /// Source-scan regression guard (same idiom as
    /// <see cref="ChatControllerErrorShapeTests"/>): zero LIVE occurrences of the leaky
    /// <c>detail: ex.Message</c> relay, and every <c>catch (WalletApiException</c>
    /// paired 1:1 with an <c>UpstreamProblem(ex)</c> call.
    /// </summary>
    [Fact]
    public void WalletController_Source_Has_No_Live_Detail_Leak_And_All_Catches_Are_Sanitized()
    {
        var path = LocateControllerSource("WalletController.cs");
        path.Should().NotBeNull(
            "src/JeebGateway/Controllers/WalletController.cs must be locatable from the test bin dir");

        var liveCode = string.Join(
            "\n",
            File.ReadAllLines(path!).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        CountOccurrences(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-253: no catch may echo the upstream ex.Message in the ProblemDetails detail field — "
            + "every upstream failure must route through UpstreamProblem, which drops the detail");

        var catches = CountOccurrences(liveCode, "catch (WalletApiException");
        var sanitized = CountOccurrences(liveCode, "UpstreamProblem(ex)");

        catches.Should().BeGreaterThan(0,
            "the guard must actually see the upstream catch sites (an emptied/renamed file must not vacuously pass)");
        sanitized.Should().Be(catches,
            "every catch (WalletApiException ...) must return UpstreamProblem(ex) — a single-site revert breaks this pairing");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string? LocateControllerSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway", "Controllers", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
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

    // The wallet management routes are capability-only ([RequireCapability(WalletManage)]),
    // so the trusted-edge X-User-Id/X-User-Roles headers authenticate an admin caller
    // (same pattern proven by ErrorShapeNormalizationTests.AdminClient).
    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "admin-jebv4-253");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
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
