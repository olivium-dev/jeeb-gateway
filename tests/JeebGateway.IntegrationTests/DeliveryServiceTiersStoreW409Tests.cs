using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// gwdbx W4-09 — the upstream tiers store's W4-11 contract: 60s snapshot cache,
/// fail-open-to-last-known on upstream failure, slug (code) identity, and CRUD
/// mapped onto delivery-service's W4-08 admin routes. TiersMode itself stays
/// "local" (inert); the freeze-import-flip rung rules are asserted at the end.
/// </summary>
public class DeliveryServiceTiersStoreW409Tests
{
    private const string CatalogJson = """
        [
          {"id":"6f000000-0000-0000-0000-000000000001","code":"urgent","name":"Urgent",
           "slaHours":2,"radius_km":5,"radiusKm":5.0,"ttl_minutes":120,"ttl_seconds":7200,
           "request_ttl_seconds":300,"commissionRate":0.1,"priceHint":"From $9",
           "createdAt":"2026-08-01T00:00:00Z","updatedAt":"2026-08-01T00:00:00Z"},
          {"id":"6f000000-0000-0000-0000-000000000002","code":"same-day","name":"Same-Day",
           "slaHours":10,"radius_km":15,"radiusKm":15.0,"ttl_minutes":600,"ttl_seconds":36000,
           "commissionRate":0.1,"priceHint":"From $5",
           "createdAt":"2026-08-01T00:00:00Z","updatedAt":"2026-08-01T00:00:00Z"}
        ]
        """;

    [Fact]
    public async Task List_Maps_Code_As_Id_And_Honours_Stored_RequestTtl()
    {
        var handler = new ScriptedHandler(_ => Ok(CatalogJson));
        var store = NewStore(handler, out _);

        var tiers = await store.ListAsync(default);

        tiers.Should().HaveCount(2);
        var urgent = tiers.Single(t => t.Id == "urgent");
        urgent.Name.Should().Be("Urgent");
        urgent.RequestTtlSeconds.Should().Be(300, "the stored request_ttl_seconds wins");
        urgent.PriceHint.Should().Be("From $9");
        var sameDay = tiers.Single(t => t.Id == "same-day");
        sameDay.RequestTtlSeconds.Should().Be(36000, "absent stored value falls back to ttl_seconds");
    }

    [Fact]
    public async Task List_Serves_From_Cache_Inside_60s_And_Refetches_After()
    {
        var handler = new ScriptedHandler(_ => Ok(CatalogJson));
        var store = NewStore(handler, out var clock);

        await store.ListAsync(default);
        await store.ListAsync(default);
        handler.Calls.Should().Be(1, "the second read inside the TTL must hit the cache");

        clock.Advance(TimeSpan.FromSeconds(61));
        await store.ListAsync(default);
        handler.Calls.Should().Be(2, "an expired snapshot must refetch");
    }

    [Fact]
    public async Task List_Fails_Open_To_Last_Known_On_Upstream_Failure()
    {
        var fail = false;
        var handler = new ScriptedHandler(_ =>
            fail ? throw new HttpRequestException("upstream down") : Ok(CatalogJson));
        var store = NewStore(handler, out var clock);

        var warm = await store.ListAsync(default);
        warm.Should().HaveCount(2);

        fail = true;
        clock.Advance(TimeSpan.FromSeconds(61));
        var stale = await store.ListAsync(default);
        stale.Should().BeEquivalentTo(warm, "a dead upstream serves the last-known snapshot");
    }

    [Fact]
    public async Task List_With_No_Snapshot_Throws_Loud_Not_Empty()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("upstream down"));
        var store = NewStore(handler, out _);

        var act = () => store.ListAsync(default);

        await act.Should().ThrowAsync<HttpRequestException>(
            "a cold store with a dead upstream must fail loud, never serve an empty catalog");
    }

    [Fact]
    public async Task Create_Puts_The_W408_Admin_Route_And_Returns_The_Refetched_Tier()
    {
        var catalog = CatalogJson;
        var handler = new ScriptedHandler(req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                catalog = catalogWithScheduled();
                return Ok("""{"code":"scheduled","outcome":"created"}""");
            }
            return Ok(catalog);
        });
        var store = NewStore(handler, out _);

        var created = await store.CreateAsync(new DeliveryTierCreate
        {
            Id = "scheduled",
            Name = "Scheduled",
            SlaHours = 48,
            RadiusKm = 30,
            RequestTtlSeconds = 900,
            CommissionRate = 0.1,
            PriceHint = "From $3",
        }, "admin-7", default);

        created.Id.Should().Be("scheduled");
        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Path.Should().Be("/admin/tiers/scheduled");
        put.Headers.Should().ContainKey("X-Actor-Ref").WhoseValue.Should().Be("admin-7");
        var body = JsonSerializer.Deserialize<JsonElement>(put.Body);
        body.GetProperty("code").GetString().Should().Be("scheduled");
        body.GetProperty("request_ttl_seconds").GetInt32().Should().Be(900);
        body.GetProperty("sla_hours").GetInt32().Should().Be(48);

        static string catalogWithScheduled() => CatalogJson.TrimEnd().TrimEnd(']') + """
            ,{"id":"6f000000-0000-0000-0000-000000000003","code":"scheduled","name":"Scheduled",
              "slaHours":48,"radius_km":30,"radiusKm":30.0,"ttl_minutes":2880,"ttl_seconds":172800,
              "request_ttl_seconds":900,"commissionRate":0.1,"priceHint":"From $3",
              "createdAt":"2026-08-01T00:00:00Z","updatedAt":"2026-08-01T00:00:00Z"}]
            """;
    }

    [Fact]
    public async Task Create_With_Existing_Code_Throws_DuplicateTierId()
    {
        var handler = new ScriptedHandler(_ => Ok(CatalogJson));
        var store = NewStore(handler, out _);

        var act = () => store.CreateAsync(new DeliveryTierCreate
        {
            Id = "urgent",
            Name = "Urgent Again",
            SlaHours = 1,
            RadiusKm = 1,
            RequestTtlSeconds = 60,
            CommissionRate = 0.1,
            PriceHint = "x",
        }, "admin-7", default);

        await act.Should().ThrowAsync<DuplicateTierIdException>();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put,
            "a duplicate id must be rejected before any upstream write");
    }

    [Fact]
    public async Task Delete_Maps_404_To_False_And_200_To_True()
    {
        var handler = new ScriptedHandler(req =>
            req.Method == HttpMethod.Delete
                ? req.Path.EndsWith("/urgent")
                    ? Ok("""{"code":"urgent","outcome":"archived"}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                : Ok(CatalogJson));
        var store = NewStore(handler, out _);

        (await store.DeleteAsync("urgent", default)).Should().BeTrue();
        (await store.DeleteAsync("no-such", default)).Should().BeFalse();
    }

    [Fact]
    public void TiersMode_FreezeImportFlip_Rungs()
    {
        // Mirrors the Program.cs Validate predicates: dual-write rungs invalid.
        static bool OnlyLocalOrAuthority(string mode) =>
            GwdbxMigrationOptions.PhaseOf(mode)
                is GwdbxMigrationPhase.Local or GwdbxMigrationPhase.UpstreamAuthority;

        OnlyLocalOrAuthority("local").Should().BeTrue();
        OnlyLocalOrAuthority("upstream-authority").Should().BeTrue();
        OnlyLocalOrAuthority("dual-write-local-read").Should().BeFalse();
        OnlyLocalOrAuthority("dual-write-upstream-read").Should().BeFalse();
        new GwdbxMigrationOptions().Tiers.Should().Be(GwdbxMigrationPhase.Local,
            "the code default ships inert");
    }

    // ---- plumbing ----------------------------------------------------------

    private static DeliveryServiceTiersStore NewStore(
        ScriptedHandler handler, out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider();
        return new DeliveryServiceTiersStore(
            new SingleClientFactory(handler, "http://delivery.test/"),
            NullLogger<DeliveryServiceTiersStore>.Instance,
            clock);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public sealed record Recorded(
            HttpMethod Method, string Path, string Body, Dictionary<string, string> Headers);

        private readonly Func<Recorded, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<Recorded, HttpResponseMessage> respond) => _respond = respond;

        public int Calls { get; private set; }

        public List<Recorded> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var recorded = new Recorded(
                request.Method,
                request.RequestUri!.AbsolutePath,
                body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
            lock (Requests)
            {
                Calls++;
                Requests.Add(recorded);
            }
            return _respond(recorded);
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string _baseUrl;

        public SingleClientFactory(HttpMessageHandler handler, string baseUrl)
        {
            _handler = handler;
            _baseUrl = baseUrl;
        }

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false) { BaseAddress = new Uri(_baseUrl) };
    }
}
