using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Tiers;

/// <summary>
/// P7 — T3: the ADMIN TTL RETROACTIVITY GUARD.
///
/// <para>The offer-wait deadline is DERIVED (<c>createdAt + tier TTL</c>), never stored.
/// That is the right ruling — but it has one honest cost: editing a tier's
/// <c>requestTtlSeconds</c> instantly MOVES the countdown of every in-flight request on
/// that tier, and the instant the sweeper will expire them. This guard does not prevent
/// that; it makes it IMPOSSIBLE TO DO SILENTLY.</para>
///
/// <para>The guard fires on an ACTUAL TTL change with no <c>applyToInFlight</c>
/// acknowledgement — including when the affected count is zero (T3.4): the contract is
/// about explicit acknowledgement, not about the size of the blast radius.</para>
/// </summary>
public class AdminTierTtlGuardTests
{
    private const string UrgentTierId = "urgent";
    private const int SeededUrgentTtlSeconds = 30 * 60;
    private const string GuardProblemType = "https://jeeb.dev/errors/tier-ttl-affects-in-flight";

    // ── T3.1 — TTL change with in-flight rows, unacknowledged → 409 ──────────

    [Fact]
    public async Task T3_1_Ttl_Change_With_Three_Pending_Rows_And_No_Ack_Is_409_With_AffectedCount()
    {
        using var factory = new WebApplicationFactory<Program>();
        for (var i = 0; i < 3; i++)
        {
            await SeedPendingAsync(factory, UrgentTierId);
        }

        var resp = await AdminClient(factory).PutAsJsonAsync(
            $"/admin/tiers/{UrgentTierId}", ReplaceBody(requestTtlSeconds: 600));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("title").GetString()
            .Should().Be("Tier TTL change affects in-flight requests");
        problem.GetProperty("type").GetString().Should().Be(GuardProblemType);
        problem.GetProperty("affectedCount").GetInt32().Should().Be(3,
            "the count is a machine-readable ProblemDetails extension so a caller never "
            + "has to parse prose");

        // The tier is UNCHANGED — a rejected guard must not half-apply.
        (await ReadTtlAsync(factory, UrgentTierId)).Should().Be(SeededUrgentTtlSeconds);
    }

    // ── T3.2 — acknowledged → 200, and the deadline really does move ─────────

    [Fact]
    public async Task T3_2_Acknowledged_Ttl_Change_Applies_And_Shortens_The_Live_Deadline()
    {
        using var factory = new WebApplicationFactory<Program>();
        var seeded = await SeedPendingAsync(factory, UrgentTierId);
        var clientId = seeded.ClientId;

        var before = await ReadDeadlineSecondsAsync(factory, clientId, seeded.Id);
        before.Should().BeGreaterThan(600, "the row starts on the seeded 30-minute window");

        var resp = await AdminClient(factory).PutAsJsonAsync(
            $"/admin/tiers/{UrgentTierId}",
            ReplaceBody(requestTtlSeconds: 600, applyToInFlight: true));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadTtlAsync(factory, UrgentTierId)).Should().Be(600);

        // This assertion DOCUMENTS the knowingly-accepted cost of derived-not-stored:
        // the in-flight row's countdown moved.
        var after = await ReadDeadlineSecondsAsync(factory, clientId, seeded.Id);
        after.Should().BeLessThanOrEqualTo(600,
            "the deadline is derived, so an acknowledged TTL change is retroactive by "
            + "construction — that is the documented cost, not a bug");
    }

    // ── T3.3 — a non-TTL edit is untouched by the guard ──────────────────────

    [Fact]
    public async Task T3_3_Name_Only_Change_With_Identical_Ttl_And_No_Ack_Is_200()
    {
        using var factory = new WebApplicationFactory<Program>();
        await SeedPendingAsync(factory, UrgentTierId);

        var resp = await AdminClient(factory).PutAsJsonAsync(
            $"/admin/tiers/{UrgentTierId}",
            ReplaceBody(requestTtlSeconds: SeededUrgentTtlSeconds, name: "Urgent (renamed)"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the guard fires ONLY on an actual TTL change — it must not become a tax on "
            + "every admin edit");
    }

    // ── T3.4 — zero affected rows still needs the acknowledgement ────────────

    [Fact]
    public async Task T3_4_Ttl_Change_With_Zero_Pending_Rows_Is_Still_409_With_AffectedCount_Zero()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await AdminClient(factory).PutAsJsonAsync(
            $"/admin/tiers/{UrgentTierId}", ReplaceBody(requestTtlSeconds: 900));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("affectedCount").GetInt32().Should().Be(0,
            "the guard is about explicit acknowledgement, not about the count");
        (await ReadTtlAsync(factory, UrgentTierId)).Should().Be(SeededUrgentTtlSeconds);
    }

    // ── guard placement: it must not shadow validation or 404 ────────────────

    [Fact]
    public async Task Guard_Does_Not_Fire_For_An_Unknown_Tier_Which_Still_404s()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await AdminClient(factory).PutAsJsonAsync(
            "/admin/tiers/does-not-exist", ReplaceBody(requestTtlSeconds: 900));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Guard_Does_Not_Shadow_Body_Validation()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await AdminClient(factory).PutAsJsonAsync(
            $"/admin/tiers/{UrgentTierId}",
            ReplaceBody(requestTtlSeconds: 900, commissionRate: 0.11));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "validation runs BEFORE the retroactivity guard");
    }

    // ── legacy-coded rows are counted against the tier they resolve to ───────

    [Fact]
    public async Task AffectedCount_Includes_Rows_Stamped_With_A_Legacy_Tier_Code()
    {
        using var factory = new WebApplicationFactory<Program>();
        await SeedPendingAsync(factory, "flash");   // legacy -> urgent
        await SeedPendingAsync(factory, "urgent");

        var resp = await AdminClient(factory).PutAsJsonAsync(
            $"/admin/tiers/{UrgentTierId}", ReplaceBody(requestTtlSeconds: 600));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("affectedCount").GetInt32().Should().Be(2,
            "a row stamped 'flash' canonicalises to 'urgent', so its deadline moves too");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static object ReplaceBody(
        int requestTtlSeconds,
        bool? applyToInFlight = null,
        string name = "Urgent",
        double commissionRate = 0.10) =>
        applyToInFlight is null
            ? new
            {
                name,
                slaHours = 1,
                radiusKm = 3.0,
                requestTtlSeconds,
                commissionRate,
                priceHint = "Premium — fastest dispatch",
            }
            : new
            {
                name,
                slaHours = 1,
                radiusKm = 3.0,
                requestTtlSeconds,
                commissionRate,
                priceHint = "Premium — fastest dispatch",
                applyToInFlight = applyToInFlight.Value,
            };

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"admin-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private static Task<DeliveryRequest> SeedPendingAsync(
        WebApplicationFactory<Program> factory, string tierId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        return store.CreateAsync(new CreateRequestInput
        {
            ClientId = $"client-{Guid.NewGuid()}",
            Description = "in-flight row",
            TierId = tierId,
            PickupLocation = new GeoPoint { Lat = 33.51, Lng = 36.27 },
            DropoffLocation = new GeoPoint { Lat = 33.50, Lng = 36.25 },
        }, default);
    }

    private static async Task<int> ReadTtlAsync(WebApplicationFactory<Program> factory, string tierId)
    {
        var raw = await factory.CreateClient().GetStringAsync("/tiers");
        return JsonDocument.Parse(raw).RootElement
            .GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == tierId)
            .GetProperty("requestTtlSeconds").GetInt32();
    }

    private static async Task<int> ReadDeadlineSecondsAsync(
        WebApplicationFactory<Program> factory, string clientId, string requestId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", clientId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync($"/v1/requests/{requestId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
            .GetProperty("offerDeadlineInSeconds").GetInt32();
    }
}
