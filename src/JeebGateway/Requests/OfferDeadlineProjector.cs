using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Requests;

/// <summary>
/// P7 (G-C) — read-side projection of the offer-wait deadline.
///
/// Singleton. Caches the merged tier-TTL map for 60 s so a per-row read never
/// becomes an upstream round-trip: the cache is MANDATORY, not an optimisation —
/// without it the list/feed endpoints acquire an upstream delivery-service
/// dependency on every page (design ace66689). Degrades to the local catalog
/// when delivery-service is unavailable, and to the last-good (or empty) map
/// when the catalog read itself faults — a projection failure must NEVER 5xx a
/// read; an empty map simply lets
/// <see cref="TierExpiryWindowResolver.ResolveExpiryWindow"/> fall back to
/// <see cref="TierExpiryWindowResolver.SafeExpiryWindow"/>.
///
/// The clock is <see cref="TimeProvider"/> — DI-registered as the singleton
/// <c>FakeTimeProvider</c> wrapping <c>TimeProvider.System</c>, so
/// <c>POST /__test/clock/advance</c> shifts this projector too. That is the E2E
/// timewarp lever; never bypass <see cref="TimeProvider"/>.
/// </summary>
public sealed class OfferDeadlineProjector
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly TierExpiryWindowResolver _windows;
    // Scoped resolution of JeebGateway.Tiers.ITiersStore — the CATALOG store, NOT
    // JeebGateway.Requests.ITiersStore (which is the create-time existence probe).
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<OfferDeadlineProjector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyDictionary<string, TimeSpan> _cached =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private bool _hasCached;

    public OfferDeadlineProjector(
        TierExpiryWindowResolver windows,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<OfferDeadlineProjector> logger)
    {
        _windows = windows;
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The ONE clock read a caller should stamp a whole response with.</summary>
    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// The merged tier-TTL map, cached for <see cref="CacheTtl"/>. Never throws:
    /// on a total load failure it serves the last good map, or an empty map when
    /// there has never been one.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, TimeSpan>> GetTtlsAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_hasCached && now - _cachedAt < CacheTtl)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Double-checked: another caller may have refreshed while we queued.
            now = _clock.GetUtcNow();
            if (_hasCached && now - _cachedAt < CacheTtl)
            {
                return _cached;
            }

            using var scope = _scopes.CreateScope();
            var tiers = scope.ServiceProvider.GetRequiredService<JeebGateway.Tiers.ITiersStore>();
            var loaded = await _windows.LoadTierTtlsAsync(tiers, ct, tolerateUpstreamFailure: true);

            _cached = loaded;
            _cachedAt = now;
            _hasCached = true;
            return _cached;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Total failure (the LOCAL catalog also threw). Serve the last good map
            // if we have one; otherwise an empty map — ResolveExpiryWindow then falls
            // back to SafeExpiryWindow and the endpoint still answers.
            _logger.LogWarning(ex,
                "Offer-deadline tier TTL load failed; serving {Source} map",
                _hasCached ? "last-good cached" : "empty");
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>(offerDeadlineAt, offerDeadlineInSeconds)</c> for ONE row at
    /// <paramref name="now"/>. Both members are null exactly when the row is not
    /// in the offer-wait window.
    /// </summary>
    public async Task<(DateTimeOffset? At, int? Seconds)> ProjectAsync(
        DeliveryRequest r,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var ttls = await GetTtlsAsync(ct);
        return Project(r, ttls, now);
    }

    /// <summary>
    /// Batch form for list/feed surfaces — resolves the TTL map ONCE per response
    /// and returns a pure lambda so decorating N rows costs no further awaits.
    /// </summary>
    public async Task<Func<DeliveryRequest, (DateTimeOffset? At, int? Seconds)>> ProjectorForAsync(
        DateTimeOffset now,
        CancellationToken ct)
    {
        var ttls = await GetTtlsAsync(ct);
        return r => Project(r, ttls, now);
    }

    private (DateTimeOffset? At, int? Seconds) Project(
        DeliveryRequest r,
        IReadOnlyDictionary<string, TimeSpan> ttls,
        DateTimeOffset now)
    {
        var at = RequestExpiryMath.DeadlineFor(r, ttls, _windows);
        return (at, RequestExpiryMath.RemainingSeconds(at, now));
    }
}
