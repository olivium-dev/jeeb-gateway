using System.Diagnostics;
using JeebGateway.Availability;
using JeebGateway.Push;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Matching;

/// <summary>
/// Reference implementation for T-backend-008. Sequencing per matching run:
///
///   1. Resolve the tier to grab its radius (km). Unknown tier → 404 at the
///      controller, never here.
///   2. Snapshot every currently-online Jeeber via
///      <see cref="IAvailabilityStore.ListOnlineAsync"/>.
///   3. Drop rows missing a GPS fix, then filter by the vehicle-type
///      allowlist, then keep only rows whose Haversine distance to the
///      pickup point is &lt;= the tier's radius.
///   4. Resolve ratings in one bulk call so the secondary sort never costs
///      an N+1.
///   5. Sort by distance ASC, rating DESC, user id ASC (stable, deterministic).
///   6. Cap at <see cref="MatchingOptions.MaxNotified"/>. The cap protects
///      push quota in dense zones without affecting candidate count, which
///      is reported separately for ops visibility.
///   7. Fan out a <see cref="NotificationTrigger.NewOffer"/> push to each
///      kept Jeeber under a single <see cref="MatchingOptions.PushFanoutSla"/>
///      deadline. Per-push failures are counted but never abort the run.
/// </summary>
public sealed class MatchingService : IMatchingService
{
    private readonly IAvailabilityStore _availability;
    private readonly ITiersStore _tiers;
    private readonly IJeeberRatingProvider _ratings;
    private readonly IPushNotificationService _push;
    private readonly IJeeberRestrictionStore _restrictions;
    private readonly TimeProvider _clock;
    private readonly MatchingOptions _options;
    private readonly ILogger<MatchingService> _log;

    public MatchingService(
        IAvailabilityStore availability,
        ITiersStore tiers,
        IJeeberRatingProvider ratings,
        IPushNotificationService push,
        IJeeberRestrictionStore restrictions,
        TimeProvider clock,
        IOptions<MatchingOptions> options,
        ILogger<MatchingService> log)
    {
        _availability = availability;
        _tiers = tiers;
        _ratings = ratings;
        _push = push;
        _restrictions = restrictions;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    public async Task<MatchingOutcome> RunAsync(MatchingInput input, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var tier = await _tiers.GetAsync(input.TierId, ct)
            ?? throw new ArgumentException($"Unknown tier '{input.TierId}'.", nameof(input));

        var online = await _availability.ListOnlineAsync(ct);

        // First pass: vehicle allowlist + GPS-fix presence + radius. Doing
        // the cheap checks before the Haversine math keeps the 10k-row
        // budget under the 500ms p99 — the inner loop is the only place
        // we touch trig, so every row excluded here saves real cycles.
        var allowAny = input.AllowedVehicleTypes.Count == 0;
        var radiusKm = tier.RadiusKm;
        var withinRadius = new List<(JeeberAvailability Row, double DistanceKm)>(capacity: Math.Min(online.Count, 256));
        var now = _clock.GetUtcNow();

        foreach (var row in online)
        {
            if (!allowAny && !input.AllowedVehicleTypes.Contains(row.VehicleType))
            {
                continue;
            }
            if (row.Latitude is null || row.Longitude is null)
            {
                continue;
            }
            // T-backend-024 (JEEB-42): a Jeeber inside an active 24-hour
            // restriction window is excluded from new offers — the
            // cancellation policy has no teeth otherwise.
            if (await _restrictions.IsRestrictedAsync(row.UserId, now, ct))
            {
                continue;
            }

            var distance = Haversine.DistanceKm(
                input.PickupLat, input.PickupLng,
                row.Latitude.Value, row.Longitude.Value);

            if (distance <= radiusKm)
            {
                withinRadius.Add((row, distance));
            }
        }

        // Bulk-resolve ratings so the secondary sort is O(1) per candidate.
        IReadOnlyDictionary<string, double> ratingMap;
        if (withinRadius.Count == 0)
        {
            ratingMap = new Dictionary<string, double>(StringComparer.Ordinal);
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (row, _) in withinRadius)
            {
                ids.Add(row.UserId);
            }
            ratingMap = await _ratings.GetRatingsAsync(ids, ct);
        }

        var ordered = withinRadius
            .Select(c => new MatchedJeeber
            {
                UserId = c.Row.UserId,
                VehicleType = c.Row.VehicleType,
                DistanceKm = c.DistanceKm,
                Rating = ratingMap.TryGetValue(c.Row.UserId, out var r) ? r : 0.0
            })
            // Proximity first (AC line 3a), rating second (3b), user id last
            // as a deterministic tiebreaker so the same input always picks
            // the same top-N — critical when two Jeebers are co-located.
            .OrderBy(c => c.DistanceKm)
            .ThenByDescending(c => c.Rating)
            .ThenBy(c => c.UserId, StringComparer.Ordinal)
            .Take(_options.MaxNotified)
            .ToList();

        // Fan out the new-offer push under a single deadline so the whole
        // run honours the 2s SLA from the AC. Per-push failures are
        // counted but never abort the run — partial completion is
        // strictly better than no offers reaching anyone.
        var notified = 0;
        if (ordered.Count > 0)
        {
            using var fanoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fanoutCts.CancelAfter(_options.PushFanoutSla);

            var tasks = new List<Task<bool>>(ordered.Count);
            foreach (var candidate in ordered)
            {
                tasks.Add(SendOneAsync(candidate, input, radiusKm, fanoutCts.Token));
            }

            try
            {
                var results = await Task.WhenAll(tasks);
                foreach (var ok in results)
                {
                    if (ok) notified++;
                }
            }
            catch (OperationCanceledException) when (fanoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The 2-second fan-out budget elapsed. Count whatever
                // tasks completed successfully and keep going — the
                // controller still returns a 200 with the partial count.
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully && task.Result)
                    {
                        notified++;
                    }
                }
                _log.LogWarning(
                    "matching fan-out exceeded {Sla}ms for request {RequestId}; notified {Notified}/{Candidates}",
                    _options.PushFanoutSla.TotalMilliseconds, input.RequestId, notified, ordered.Count);
            }
        }

        sw.Stop();

        return new MatchingOutcome
        {
            RequestId = input.RequestId,
            TierId = tier.Id,
            RadiusKm = radiusKm,
            NotifiedCount = notified,
            Candidates = ordered,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<bool> SendOneAsync(
        MatchedJeeber candidate,
        MatchingInput input,
        double radiusKm,
        CancellationToken ct)
    {
        var push = new PushNotificationRequest(
            UserId: candidate.UserId,
            Trigger: NotificationTrigger.NewOffer,
            Title: "New delivery offer",
            Body: $"A pickup is waiting {candidate.DistanceKm:0.0} km away.",
            Data: new Dictionary<string, string>
            {
                ["requestId"] = input.RequestId,
                ["tierId"] = input.TierId,
                ["distanceKm"] = candidate.DistanceKm.ToString("0.000"),
                ["radiusKm"] = radiusKm.ToString("0.000")
            },
            IdempotencyKey: $"match:{input.RequestId}:{candidate.UserId}");

        try
        {
            var result = await _push.SendAsync(push, ct);
            return result.Outcome is PushDeliveryOutcome.Delivered
                or PushDeliveryOutcome.DeliveredOnRetry
                or PushDeliveryOutcome.QueuedForRetry;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "matching push failed for jeeber {UserId} on request {RequestId}",
                candidate.UserId, input.RequestId);
            return false;
        }
    }
}
