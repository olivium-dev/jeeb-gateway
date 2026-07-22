using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using JeebGateway.Services;
using JeebGateway.Services.Clients;

namespace JeebGateway.Requests;

public sealed class TierExpiryWindowResolver
{
    public static readonly TimeSpan SafeExpiryWindow = TimeSpan.FromHours(24);

    private readonly IOptionsMonitor<UpstreamFeatureFlags> _upstreamFlags;
    private readonly IDeliveryServiceClient _delivery;
    private readonly ILogger<TierExpiryWindowResolver> _logger;

    public TierExpiryWindowResolver(
        IOptionsMonitor<UpstreamFeatureFlags> upstreamFlags,
        IDeliveryServiceClient delivery,
        ILogger<TierExpiryWindowResolver> logger)
    {
        _upstreamFlags = upstreamFlags;
        _delivery = delivery;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, TimeSpan>> LoadTierTtlsAsync(
        JeebGateway.Tiers.ITiersStore tiers,
        CancellationToken ct)
    {
        // Two tier-id vocabularies reach this sweeper and BOTH must resolve:
        // the local catalog's slugs (urgent/same-day/scheduled — what
        // `LegacyTierCodes.Canonicalize` produces for a request stamped with a
        // legacy code like "flash") and delivery-service's UUIDs (what a
        // request created through the upstream path carries). Loading only one
        // of them silently pushes every request of the other shape onto the 24h
        // safe fallback — the bug this class had for the UUID shape, which the
        // slug shape would inherit if the upstream catalog simply replaced the
        // local one. Seed with the local slugs, then overlay upstream so the
        // live service stays authoritative wherever the two ever collide.
        var merged = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        var localCatalog = await tiers.ListAsync(ct);
        foreach (var tier in localCatalog.Where(t => t.RequestTtlSeconds > 0))
        {
            merged[tier.Id] = TimeSpan.FromSeconds(tier.RequestTtlSeconds);
        }

        if (_upstreamFlags.CurrentValue.Delivery)
        {
            var upstreamCatalog = await _delivery.ListTiersAsync(ct);
            foreach (var tier in upstreamCatalog.Where(t => t.RequestTtlSeconds > 0))
            {
                merged[tier.Id] = TimeSpan.FromSeconds(tier.RequestTtlSeconds);
            }
        }

        return merged;
    }

    public TimeSpan ResolveExpiryWindow(
        DeliveryRequest req,
        IReadOnlyDictionary<string, TimeSpan> tierTtls)
    {
        var tierId = req.TierId ?? string.Empty;
        var canonicalTierId = JeebGateway.Tiers.LegacyTierCodes.Canonicalize(tierId);

        if (!string.IsNullOrWhiteSpace(canonicalTierId)
            && tierTtls.TryGetValue(canonicalTierId, out var ttl))
        {
            return ttl;
        }

        if (tierTtls.TryGetValue(JeebGateway.Tiers.InMemoryTiersStore.DefaultExpiryTierId, out var fallback))
        {
            _logger.LogWarning(
                "Request {RequestId} has unknown tier {TierId}; using default tier TTL {WindowMinutes}m",
                req.Id,
                string.IsNullOrWhiteSpace(tierId) ? "<empty>" : tierId,
                fallback.TotalMinutes);
            return fallback;
        }

        fallback = SafeExpiryWindow;
        _logger.LogWarning(
            "Request {RequestId} has unknown tier {TierId}; default tier TTL is unavailable, using safe TTL {WindowMinutes}m",
            req.Id,
            string.IsNullOrWhiteSpace(tierId) ? "<empty>" : tierId,
            fallback.TotalMinutes);
        return fallback;
    }
}
