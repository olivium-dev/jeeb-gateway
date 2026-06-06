using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// S03 H2/H3 (DEC1) — the CDN signed-PUT broker endpoint POST /api/cdn/assets.
/// Pins the snake_case contract {upload_url, object_ref, expires_in≤300}, the
/// slot/content-type validation, the auth gate, and the flag-off 503 kill switch.
///
/// The broker requires FeatureFlags:UseUpstream:Cdn ON; these tests stand up a
/// stub <see cref="ICDNServiceClient"/> so they are CI-safe without a live
/// cdn-service, exactly like CdnServiceClientContractTests does for the client.
/// </summary>
public sealed class CdnUploadBrokerEndpointTests
{
    [Fact]
    public async Task BrokerUploadUrl_Happy_Returns_200_With_Snake_Case_Ticket_And_Bounded_Ttl()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "s03-cdn-happy");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("upload_url").GetString().Should().StartWith("https://");
        json.GetProperty("object_ref").GetString().Should().NotBeNullOrWhiteSpace();
        // BR-2: expires_in must be ≤ 300.
        json.GetProperty("expires_in").GetInt32().Should().BeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task BrokerUploadUrl_Clamps_Upstream_Ttl_Above_300_To_300()
    {
        using var factory = CdnEnabledFactory(new StubCdn { ExpiresInSeconds = 999 });
        var client = ClientFor(factory, "s03-cdn-clamp");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "selfie_with_liveness",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("expires_in").GetInt32().Should().Be(300);
    }

    [Fact]
    public async Task BrokerUploadUrl_Unknown_Slot_Returns_400()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "s03-cdn-badslot");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "not_a_real_slot",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BrokerUploadUrl_Without_Identity_Returns_401()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BrokerUploadUrl_Flag_Off_Returns_503_KillSwitch()
    {
        // Default factory — Cdn flag is off in the test (appsettings.json) env.
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "s03-cdn-off");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> CdnEnabledFactory(ICDNServiceClient stub) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Cdn", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICDNServiceClient>();
                services.AddSingleton(stub);
            });
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    private sealed class StubCdn : ICDNServiceClient
    {
        public int ExpiresInSeconds { get; init; } = 300;

        public Task<CdnUploadTicket> MintUploadUrlAsync(CdnUploadUrlRequest request, CancellationToken ct)
            => Task.FromResult(new CdnUploadTicket
            {
                UploadUrl = $"https://cdn.jeeb.lb/put/{request.Slot}?sig=abc",
                ObjectRef = $"cdn://obj/{request.Slot}/{Guid.NewGuid():N}",
                ExpiresInSeconds = ExpiresInSeconds,
            });

        public Task<CdnAsset> UploadAsync(CdnUploadRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnSignedUrl> GetSignedUrlAsync(string assetId, int ttlSeconds, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnAsset?> GetAssetAsync(string assetId, CancellationToken ct)
            => Task.FromResult<CdnAsset?>(null);
    }
}
