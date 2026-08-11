using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Tiers;

/// <summary>
/// O11 — the client's own order list rendered no tier chip and no <c>ORD-…</c> reference.
///
/// <para>Root cause is a PAYLOAD gap, not a client bug: the order rows carry only <c>tierId</c>,
/// which since the delivery-service cut-over is a UUIDv5 that matches no client tier lexicon, and
/// the legacy <c>GET /requests?role=client</c> surface carried no display reference at all. Both
/// list surfaces now project a resolved tier token plus the order reference; the raw
/// <c>tierId</c> is preserved beside them.</para>
/// </summary>
public sealed class OrderTierDisplayContractTests
{
    private const string StandardId = "2bd0d5df-db76-5d14-9e4d-741d60b2fa12";

    [Fact]
    public async Task LegacyRequestsList_ProjectsTheDisplayTierAndOrderReference()
    {
        using var factory = Factory();
        var client = ClientFor(factory, out var clientId);
        var seeded = await SeedAsync(factory, clientId, StandardId);

        var row = (await ReadArrayAsync(client, "/requests?role=client"))
            .Should().ContainSingle(r => Str(r, "id") == seeded.Id).Subject;

        Str(row, "tier").Should().Be("standard", "the client tier lexicon is the tier NAME, lowercased");
        Str(row, "tierName").Should().Be("Standard");
        Str(row, "tierId").Should().Be(StandardId, "the raw id is preserved, not replaced");
        Str(row, "displayId").Should().StartWith("ORD-").And.HaveLength(10);
    }

    [Fact]
    public async Task V1RequestsList_ProjectsTheDisplayTierInsteadOfTheRawGuid()
    {
        using var factory = Factory();
        var client = ClientFor(factory, out var clientId);
        var seeded = await SeedAsync(factory, clientId, StandardId);

        var page = await ReadJsonAsync(client, "/v1/requests?role=client");
        var row = page.GetProperty("items").EnumerateArray()
            .Should().ContainSingle(r => Str(r, "id") == seeded.Id).Subject;

        Str(row, "tier").Should().Be("standard",
            "`tier` echoed the raw UUID, so every order card fell through to an unknown tier");
        Str(row, "tierId").Should().Be(StandardId);
        Str(row, "tierName").Should().Be("Standard");
    }

    [Fact]
    public async Task AnUnresolvableTier_FallsBackToTheRawIdRatherThanDroppingIt()
    {
        using var factory = Factory();
        var client = ClientFor(factory, out var clientId);
        var seeded = await SeedAsync(factory, clientId, "11111111-2222-3333-4444-555555555555");

        var row = (await ReadArrayAsync(client, "/requests?role=client"))
            .Should().ContainSingle(r => Str(r, "id") == seeded.Id).Subject;

        Str(row, "tier").Should().Be("11111111-2222-3333-4444-555555555555");
        row.TryGetProperty("tierName", out _).Should().BeFalse("an unresolved tier has no name");
    }

    [Fact]
    public void TheOrderReference_MatchesTheClientSideDerivation()
    {
        // The clients already derive ORD-<last 6 alphanumerics, uppercased> when the field is
        // absent; the server-sent value must be the same string or the header would flicker.
        TierDisplay.OrderReference("3f2a1b9c-0d4e-4f60-9a1b-77c0de38d786").Should().Be("ORD-38D786");
        TierDisplay.OrderReference("abc").Should().Be("ORD-ABC");
        TierDisplay.OrderReference("   ").Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string? Str(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new UpstreamCatalogClient());
            });
        });

    private sealed class UpstreamCatalogClient : FakeDeliveryPresenceClient
    {
        public override Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
        {
            IReadOnlyList<DeliveryTierDto> rows = new[]
            {
                new DeliveryTierDto
                {
                    Id = StandardId,
                    Name = "Standard",
                    SlaHours = 24,
                    RadiusKm = 25.0,
                    RequestTtlSeconds = 86400,
                    CommissionRate = 0.10,
                    PriceHint = "Standard",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    UpdatedAt = DateTimeOffset.UnixEpoch,
                },
            };
            return Task.FromResult(rows);
        }
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, out string clientId)
    {
        clientId = $"client-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", clientId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return client;
    }

    private static Task<DeliveryRequest> SeedAsync(
        WebApplicationFactory<Program> factory, string clientId, string tierId)
        => factory.Services.GetRequiredService<IRequestsStore>().CreateAsync(
            new CreateRequestInput
            {
                ClientId = clientId,
                Description = "2 kg tomatoes",
                TierId = tierId,
                PickupAddress = "Pickup",
                DropoffAddress = "Dropoff",
            },
            default);

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<IEnumerable<JsonElement>> ReadArrayAsync(HttpClient client, string path)
        => (await ReadJsonAsync(client, path)).EnumerateArray();
}
