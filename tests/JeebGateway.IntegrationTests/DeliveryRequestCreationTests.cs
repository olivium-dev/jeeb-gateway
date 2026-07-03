using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-007: POST /requests now requires tierId, pickupLocation,
/// dropoffLocation, and validates audioUrl / photos[] shape. The existing
/// RequestsEndpointTests cover BR-9 (3-active cap) and basic happy-path
/// creation; this file owns the new field-level validation and the
/// payload-persistence acceptance criterion.
/// </summary>
public class DeliveryRequestCreationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeliveryRequestCreationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_With_All_Fields_Persists_Every_Value()
    {
        var client = ClientFor("t007-full-payload");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "Pick up a parcel from the warehouse",
            transcription = "raw STT: pick up a parsel from the warehouse",
            audioUrl = "https://audio.jeeb.dev/clips/abc.opus",
            photos = new[]
            {
                "https://media.jeeb.dev/p/1.jpg",
                "https://media.jeeb.dev/p/2.jpg"
            },
            tierId = "flash",
            pickupLocation = new { lat = 24.7136, lng = 46.6753 },   // Riyadh
            dropoffLocation = new { lat = 24.6309, lng = 46.7194 },  // DQ
            pickupAddress = "Carrefour Riyadh Park",
            dropoffAddress = "Diplomatic Quarter"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();

        dto.Should().NotBeNull();
        dto!.Id.Should().NotBeNullOrWhiteSpace();
        dto.Status.Should().Be(RequestStatus.Pending);
        dto.Description.Should().Be("Pick up a parcel from the warehouse");
        dto.Transcription.Should().Be("raw STT: pick up a parsel from the warehouse");
        dto.AudioUrl.Should().Be("https://audio.jeeb.dev/clips/abc.opus");
        dto.Photos.Should().BeEquivalentTo(new[]
        {
            "https://media.jeeb.dev/p/1.jpg",
            "https://media.jeeb.dev/p/2.jpg"
        });
        dto.TierId.Should().Be("flash");
        dto.PickupLocation.Should().NotBeNull();
        dto.PickupLocation!.Lat.Should().Be(24.7136);
        dto.PickupLocation.Lng.Should().Be(46.6753);
        dto.DropoffLocation.Should().NotBeNull();
        dto.DropoffLocation!.Lat.Should().Be(24.6309);
        dto.PickupAddress.Should().Be("Carrefour Riyadh Park");
        dto.DropoffAddress.Should().Be("Diplomatic Quarter");
    }

    [Fact]
    public async Task Create_Without_TierId_Returns_400()
    {
        var client = ClientFor("t007-no-tier");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "no tier here",
            pickupLocation = new { lat = 24.7136, lng = 46.6753 },
            dropoffLocation = new { lat = 24.6309, lng = 46.7194 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/tier-required");
    }

    [Fact]
    public async Task Create_With_Unknown_Tier_Returns_400()
    {
        var client = ClientFor("t007-bad-tier");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "unknown tier",
            tierId = "platinum_super_fast",
            pickupLocation = new { lat = 24.7136, lng = 46.6753 },
            dropoffLocation = new { lat = 24.6309, lng = 46.7194 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/tier-not-found");
    }

    [Theory]
    [InlineData("flash")]
    [InlineData("express")]
    [InlineData("standard")]
    [InlineData("on_the_way")]
    [InlineData("eco")]
    public async Task Create_Accepts_Every_Seeded_Tier_Code(string tierCode)
    {
        var client = ClientFor($"t007-tier-{tierCode}");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = $"tier check {tierCode}",
            tierId = tierCode,
            pickupLocation = new { lat = 24.7136, lng = 46.6753 },
            dropoffLocation = new { lat = 24.6309, lng = 46.7194 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        dto!.TierId.Should().Be(tierCode);
    }

    [Fact]
    public async Task Create_Without_Locations_Returns_400()
    {
        var client = ClientFor("t007-no-locations");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "missing locations",
            tierId = "flash"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/location-required");
    }

    [Theory]
    [InlineData(-91.0, 0.0)]      // lat under -90
    [InlineData(90.1, 0.0)]       // lat over 90
    [InlineData(0.0, -180.5)]     // lng under -180
    [InlineData(0.0, 181.0)]      // lng over 180
    public async Task Create_With_Out_Of_Range_Coords_Returns_400(double badLat, double badLng)
    {
        var client = ClientFor($"t007-badcoord-{badLat}-{badLng}");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "bad coords",
            tierId = "flash",
            pickupLocation = new { lat = badLat, lng = badLng },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/location-invalid");
    }

    [Fact]
    public async Task Create_With_Malformed_AudioUrl_Returns_400()
    {
        var client = ClientFor("t007-bad-audio");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "bad audio",
            tierId = "flash",
            audioUrl = "not a url",
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/audio-url-invalid");
    }

    [Fact]
    public async Task Create_With_Bad_Photo_Entry_Returns_400()
    {
        var client = ClientFor("t007-bad-photo");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "bad photo",
            tierId = "flash",
            photos = new[] { "https://ok.example/1.jpg", "ftp://nope.example/2.jpg" },
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/photo-url-invalid");
    }

    [Fact]
    public async Task Create_With_Too_Many_Photos_Returns_400()
    {
        var client = ClientFor("t007-photo-flood");
        var photos = Enumerable.Range(0, 11)
            .Select(i => $"https://media.jeeb.dev/p/{i}.jpg")
            .ToArray();

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "too many photos",
            tierId = "flash",
            photos,
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/photos-too-many");
    }

    [Fact]
    public async Task Create_Without_Optional_Audio_Photos_Still_Succeeds()
    {
        var client = ClientFor("t007-no-media");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "text-only request",
            tierId = "standard",
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        dto!.AudioUrl.Should().BeNull();
        dto.Transcription.Should().BeNull();
        dto.Photos.Should().BeEmpty();
    }

    /// <summary>
    /// Acceptance criterion: request expires after 30 minutes if no offer
    /// is accepted. The sweeper itself has its own unit-test file
    /// (RequestExpirySweeperTests) — here we just lock in the default
    /// 30-min window so a future config tweak that violates the ticket
    /// surfaces as a failed test rather than silent regression.
    /// </summary>
    [Fact]
    public void Default_Expiry_Window_Is_30_Minutes()
    {
        var defaults = new RequestExpiryOptions();
        defaults.ExpiryWindow.Should().Be(TimeSpan.FromMinutes(30));
        defaults.NoOfferNudgeWindow.Should().BeLessThan(defaults.ExpiryWindow);
    }

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }
}

/// <summary>
/// Pure unit tests for the create-time tier-existence probe — no host required.
/// feat/tier-unify-names: the probe is now catalog-backed
/// (<see cref="CatalogBackedTiersStore"/> over <see cref="JeebGateway.Tiers.InMemoryTiersStore"/>),
/// so it accepts BOTH the catalog ids (urgent/same-day/…) and the mapped
/// legacy 0011 codes (flash/express/standard/on_the_way/eco). Lookups are
/// case-insensitive, matching the catalog store's id semantics.
/// </summary>
public class InMemoryTiersStoreTests
{
    private static CatalogBackedTiersStore NewStore()
        => new(new JeebGateway.Tiers.InMemoryTiersStore());

    [Theory]
    // Legacy 0011 codes — accepted via the LegacyTierCodes alias table.
    [InlineData("flash")]
    [InlineData("express")]
    [InlineData("standard")]
    [InlineData("on_the_way")]
    [InlineData("eco")]
    // Catalog ids — the single source of truth, accepted directly.
    [InlineData("urgent")]
    [InlineData("same-day")]
    [InlineData("scheduled")]
    [InlineData("economy")]
    [InlineData("on-the-way")]
    // Case-insensitive, matching the catalog store's id semantics.
    [InlineData("FLASH")]
    [InlineData("Urgent")]
    public async Task ExistsAsync_Returns_True_For_Seeded_Codes(string code)
    {
        var store = NewStore();
        (await store.ExistsAsync(code, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("platinum_super_fast")]
    [InlineData("nope")]
    public async Task ExistsAsync_Returns_False_For_Unknown_Or_Blank(string code)
    {
        var store = NewStore();
        (await store.ExistsAsync(code, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_A_Catalog_Row_Retires_Its_Legacy_Aliases_Too()
    {
        // Single-source-of-truth proof: retiring "urgent" from the catalog must
        // also retire the legacy codes that alias to it (flash, express).
        var catalog = new JeebGateway.Tiers.InMemoryTiersStore();
        var store = new CatalogBackedTiersStore(catalog);

        (await store.ExistsAsync("flash", CancellationToken.None)).Should().BeTrue();

        (await catalog.DeleteAsync("urgent", CancellationToken.None)).Should().BeTrue();

        (await store.ExistsAsync("urgent", CancellationToken.None)).Should().BeFalse();
        (await store.ExistsAsync("flash", CancellationToken.None)).Should().BeFalse();
        (await store.ExistsAsync("express", CancellationToken.None)).Should().BeFalse();
        // Tiers aliased to OTHER catalog rows are untouched.
        (await store.ExistsAsync("standard", CancellationToken.None)).Should().BeTrue();
    }
}

/// <summary>
/// Pure unit tests for the WGS84 validator on <see cref="GeoPoint"/>.
/// Bounds-only logic kept here rather than in the integration test so it
/// runs as a fast feedback loop on every build.
/// </summary>
public class GeoPointTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(90.0, 180.0)]
    [InlineData(-90.0, -180.0)]
    [InlineData(24.7136, 46.6753)]
    public void IsValid_True_For_InRange(double lat, double lng)
    {
        new GeoPoint { Lat = lat, Lng = lng }.IsValid().Should().BeTrue();
    }

    [Theory]
    [InlineData(90.0001, 0.0)]
    [InlineData(-90.0001, 0.0)]
    [InlineData(0.0, 180.0001)]
    [InlineData(0.0, -180.0001)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(0.0, double.NaN)]
    public void IsValid_False_For_OutOfRange_Or_NaN(double lat, double lng)
    {
        new GeoPoint { Lat = lat, Lng = lng }.IsValid().Should().BeFalse();
    }
}
