using JeebGateway.Availability;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Admin operations dashboard — exposes the currently-online Jeebers
/// grouped by configurable zone boundaries (T-backend-051). The
/// ops-map client polls this endpoint every 30 seconds; the cache
/// directive advertises that cadence to intermediaries.
/// </summary>
[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("admin/zones")]
[RequireRole(Roles.Admin)]
public class AdminZonesController : ControllerBase
{
    private readonly IAvailabilityStore _store;
    private readonly IOptionsMonitor<ZoneOptions> _zones;
    private readonly TimeProvider _clock;

    public AdminZonesController(
        IAvailabilityStore store,
        IOptionsMonitor<ZoneOptions> zones,
        TimeProvider clock)
    {
        _store = store;
        _zones = zones;
        _clock = clock;
    }

    [HttpGet("online-jeebers")]
    [ProducesResponseType(typeof(AdminZoneViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOnlineJeebers(CancellationToken ct)
    {
        var options = _zones.CurrentValue;
        var online = await _store.ListOnlineAsync(ct);

        var buckets = new Dictionary<string, List<JeeberAvailability>>(StringComparer.Ordinal);
        foreach (var boundary in options.Boundaries)
        {
            buckets[boundary.Key] = new List<JeeberAvailability>();
        }
        buckets[ZoneOptions.UnzonedKey] = new List<JeeberAvailability>();

        foreach (var jeeber in online)
        {
            var key = ResolveZoneKey(jeeber, options.Boundaries);
            buckets[key].Add(jeeber);
        }

        var groups = new List<AdminZoneGroup>(options.Boundaries.Count + 1);
        foreach (var boundary in options.Boundaries)
        {
            groups.Add(BuildGroup(boundary.Key, boundary.Name, boundary, buckets[boundary.Key]));
        }
        groups.Add(BuildGroup(ZoneOptions.UnzonedKey, "Outside configured zones", null, buckets[ZoneOptions.UnzonedKey]));

        var response = new AdminZoneViewResponse
        {
            Zones = groups,
            TotalOnline = online.Count,
            GeneratedAt = _clock.GetUtcNow(),
            RefreshIntervalSeconds = (int)Math.Round(options.RefreshInterval.TotalSeconds)
        };

        Response.Headers.CacheControl = $"public, max-age={response.RefreshIntervalSeconds}";
        return Ok(response);
    }

    private static string ResolveZoneKey(
        JeeberAvailability jeeber,
        IReadOnlyList<ZoneBoundary> boundaries)
    {
        if (jeeber.Latitude is null || jeeber.Longitude is null) return ZoneOptions.UnzonedKey;

        foreach (var boundary in boundaries)
        {
            if (boundary.Contains(jeeber.Latitude.Value, jeeber.Longitude.Value))
            {
                return boundary.Key;
            }
        }

        return ZoneOptions.UnzonedKey;
    }

    private static AdminZoneGroup BuildGroup(
        string key,
        string? name,
        ZoneBoundary? boundary,
        IReadOnlyList<JeeberAvailability> members)
    {
        var byVehicle = members
            .GroupBy(m => m.VehicleType.ToWire(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var markers = members.Select(m => new AdminJeeberMarker
        {
            UserId = m.UserId,
            VehicleType = m.VehicleType.ToWire(),
            Latitude = m.Latitude,
            Longitude = m.Longitude,
            LastSeenAt = m.LastSeenAt
        }).ToList();

        return new AdminZoneGroup
        {
            Key = key,
            Name = name,
            Bounds = boundary is null ? null : new ZoneBoundsDto
            {
                MinLatitude = boundary.MinLatitude,
                MaxLatitude = boundary.MaxLatitude,
                MinLongitude = boundary.MinLongitude,
                MaxLongitude = boundary.MaxLongitude
            },
            Count = members.Count,
            CountByVehicleType = byVehicle,
            Jeebers = markers
        };
    }
}
