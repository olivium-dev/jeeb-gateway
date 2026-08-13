using System.Collections.Concurrent;

namespace JeebGateway.Availability;

public class InMemoryAvailabilityStore : IAvailabilityStore
{
    private readonly ConcurrentDictionary<string, JeeberAvailability> _records = new();
    private readonly IGeoIndex _geo;
    private readonly IPendingOffersStore _offers;
    private readonly TimeProvider _clock;

    public InMemoryAvailabilityStore(IGeoIndex geo, IPendingOffersStore offers, TimeProvider clock)
    {
        _geo = geo;
        _offers = offers;
        _clock = clock;
    }

    public Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
    {
        var record = _records.GetOrAdd(userId, NewDefault);
        return Task.FromResult(Clone(record));
    }

    public async Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var wasAlreadyOnline = false;

        var record = _records.AddOrUpdate(
            userId,
            _ =>
            {
                var fresh = NewDefault(userId);
                Apply(fresh, request, now);
                return fresh;
            },
            (_, existing) =>
            {
                wasAlreadyOnline = existing.IsOnline;
                Apply(existing, request, now);
                return existing;
            });

        await _geo.AddAsync(userId, record.VehicleType, record.Longitude, record.Latitude, ct);

        return new GoOnlineResult
        {
            Availability = Clone(record),
            WasAlreadyOnline = wasAlreadyOnline
        };
    }

    public async Task<GoOfflineResult> GoOfflineAsync(string userId, GoOfflineReason reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var wasOnline = false;

        var record = _records.AddOrUpdate(
            userId,
            _ =>
            {
                var fresh = NewDefault(userId);
                fresh.IsOnline = false;
                fresh.UpdatedAt = now;
                fresh.LastInteractionAt = reason == GoOfflineReason.UserToggle ? now : fresh.LastInteractionAt;
                return fresh;
            },
            (_, existing) =>
            {
                wasOnline = existing.IsOnline;
                existing.IsOnline = false;
                existing.UpdatedAt = now;
                if (reason == GoOfflineReason.UserToggle)
                {
                    existing.LastInteractionAt = now;
                }
                return existing;
            });

        await _geo.RemoveAsync(userId, ct);
        // Best-effort in lockstep with PostgresAvailabilityStore: the offline flip is
        // already applied, so a withdraw with no upstream route must not abort the caller.
        int withdrawn;
        try
        {
            withdrawn = await _offers.WithdrawForJeeberAsync(userId, ct);
        }
        catch (NotSupportedException)
        {
            withdrawn = 0;
        }

        return new GoOfflineResult
        {
            Availability = Clone(record),
            WithdrawnOffers = withdrawn,
            WasOnline = wasOnline
        };
    }

    public Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        // Touch-existing-only, in lockstep with PostgresAvailabilityStore: a plain GET must
        // never seed the roster — row creation is GoOnlineAsync's alone.
        if (_records.TryGetValue(userId, out var existing))
        {
            existing.LastInteractionAt = at;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct)
    {
        IReadOnlyList<JeeberAvailability> snapshot = _records.Values
            .Where(r => r.IsOnline)
            .Select(Clone)
            .ToArray();
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<JeeberAvailability>> ListKnownJeebersAsync(DateTimeOffset since, CancellationToken ct)
    {
        // P1 — the never-starve fallback source for the new-request fan-out. Mirrors the
        // Postgres COALESCE(last_interaction_at, last_seen_at, updated_at) >= @Since predicate.
        IReadOnlyList<JeeberAvailability> snapshot = _records.Values
            .Where(r => (r.LastInteractionAt ?? r.LastSeenAt ?? r.UpdatedAt) >= since)
            .Select(Clone)
            .ToArray();
        return Task.FromResult(snapshot);
    }

    private static void Apply(JeeberAvailability record, GoOnlineRequest request, DateTimeOffset now)
    {
        record.IsOnline = true;
        record.VehicleType = request.VehicleType;
        record.Zone = request.Zone;
        if (request.Longitude is not null) record.Longitude = request.Longitude;
        if (request.Latitude is not null) record.Latitude = request.Latitude;
        record.LastSeenAt = now;
        record.LastInteractionAt = now;
        record.UpdatedAt = now;
    }

    private JeeberAvailability NewDefault(string userId) => new()
    {
        UserId = userId,
        IsOnline = false,
        VehicleType = VehicleType.Car,
        Zone = null,
        UpdatedAt = _clock.GetUtcNow()
    };

    private static JeeberAvailability Clone(JeeberAvailability r) => new()
    {
        UserId = r.UserId,
        IsOnline = r.IsOnline,
        VehicleType = r.VehicleType,
        Zone = r.Zone,
        Longitude = r.Longitude,
        Latitude = r.Latitude,
        LastSeenAt = r.LastSeenAt,
        LastInteractionAt = r.LastInteractionAt,
        UpdatedAt = r.UpdatedAt
    };
}
