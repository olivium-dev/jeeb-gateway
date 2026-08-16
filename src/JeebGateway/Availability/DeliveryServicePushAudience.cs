using JeebGateway.Services.Clients;

namespace JeebGateway.Availability;

/// <summary>
/// OA-21 — <see cref="IPushAudienceSource"/> over delivery-service, the presence
/// owner. Holds NO state: every call is a read of the owning service, so a gateway
/// restart cannot empty the audience.
///
/// <para>It translates upstream rows into <see cref="JeeberAvailability"/> so the
/// fan-out's existing geo cut, initiator exclusion and role re-check are unchanged,
/// and it converts ANY read failure into
/// <see cref="PushAudienceUnavailableException"/> so the caller can tell an
/// unreadable audience from an empty one.</para>
/// </summary>
public sealed class DeliveryServicePushAudience : IPushAudienceSource
{
    /// <summary>Rung labels carried on the failure so the error line names the read.</summary>
    public const string AvailableRung = "available";
    public const string ReachableRung = "reachable";

    private readonly IDeliveryServiceClient _owner;

    public DeliveryServicePushAudience(IDeliveryServiceClient owner) => _owner = owner;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JeeberAvailability>> ListAvailableAsync(CancellationToken ct)
    {
        // No circle here on purpose: the caller applies the request's OWN tier
        // radius so it can still report the nearest distance when the cut empties.
        IReadOnlyList<AvailableProviderUpstream> rows;
        try
        {
            rows = await _owner.ListAvailableProvidersAsync(null, null, null, null, 0, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PushAudienceUnavailableException(AvailableRung, ex);
        }

        var mapped = new List<JeeberAvailability>(rows.Count);
        foreach (var row in rows)
        {
            var id = row.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            _ = VehicleTypeExtensions.TryParseWire(row.VehicleType, out var vehicle);
            mapped.Add(new JeeberAvailability
            {
                UserId = id,
                IsOnline = true,
                VehicleType = vehicle,
                Latitude = row.Lat,
                Longitude = row.Lng,
            });
        }

        return mapped;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JeeberAvailability>> ListReachableSinceAsync(
        DateTimeOffset since, CancellationToken ct)
    {
        IReadOnlyList<JeeberAvailabilityUpstream> rows;
        try
        {
            rows = await _owner.ListKnownProvidersAsync(since, 0, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PushAudienceUnavailableException(ReachableRung, ex);
        }

        var mapped = new List<JeeberAvailability>(rows.Count);
        foreach (var row in rows)
        {
            var id = row.JeeberId?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            _ = VehicleTypeExtensions.TryParseWire(row.VehicleType, out var vehicle);
            mapped.Add(new JeeberAvailability
            {
                UserId = id,
                IsOnline = row.Online,
                VehicleType = vehicle,
                Zone = row.Zone,
                Latitude = row.Lat,
                Longitude = row.Lng,
                LastSeenAt = row.LastSeenAt,
                LastInteractionAt = row.LastSeenAt,
                UpdatedAt = row.UpdatedAt,
            });
        }

        return mapped;
    }
}
