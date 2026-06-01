using JeebGateway.Services.Clients;

namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// Upstream-backed implementation of <see cref="IJeeberRestrictionStore"/> that
/// proxies the real ban-service (Rust / Actix-Web, Redis-backed, host port
/// 10065) via <see cref="IBanServiceClient"/>. Selected over
/// <see cref="InMemoryJeeberRestrictionStore"/> at DI time when
/// <c>FeatureFlags:UseUpstream:Ban</c> is true.
///
/// Because <see cref="CancellationService"/> (the single consumer of this
/// interface) is invoked from BOTH
/// <c>Controllers/AdminCancellationsController</c> and
/// <c>Controllers/DeliveriesController</c>, swapping the implementation here
/// gates the restriction record-of-truth behind the Ban flag across BOTH call
/// sites with no controller-level branching.
///
/// <para><b>Semantic mapping — read first.</b> The gateway's restriction model
/// is a single 24-hour "no new offers" block keyed by jeeberId. ban-service
/// models a PROGRESSIVE ban (WARNING → PARTIAL_BAN → BAN) whose durations are
/// owned by ban-service's <c>banning-rule.json</c>, NOT by the caller. This
/// wrapper therefore:</para>
/// <list type="bullet">
///   <item><b>IsRestrictedAsync / GetActiveExpiryAsync</b> (read): derive from
///   <c>GET /api/v1/ban/{id}/status</c> — restricted iff any ban row reports
///   <c>is_currently_banned</c>; expiry is the latest non-null
///   <c>banned_until</c>. The <paramref name="at"/> argument is honoured by
///   comparing against the upstream-reported expiry so a just-expired ban that
///   ban-service has not yet swept reads as un-restricted.</item>
///   <item><b>ApplyAsync</b> (write): advances the <c>yellow</c> progression via
///   <c>POST /api/v1/ban/{id}/yellow</c>. The gateway's requested
///   <paramref name="duration"/> is advisory only — ban-service assigns the
///   actual <c>banned_until</c> from its stage rules. This is a deliberate
///   record-of-truth handover: ban-service, not the gateway, owns ban durations
///   and progression once the Ban flag is on.</item>
/// </list>
/// </summary>
public sealed class BanServiceJeeberRestrictionStore : IJeeberRestrictionStore
{
    /// <summary>
    /// The ban-service progression used to record a Jeeber abuse-control
    /// restriction. <c>yellow</c> is the graduated progression
    /// (WARNING → PARTIAL_BAN → BAN); the cancellation-threshold trigger maps to
    /// advancing this progression rather than an immediate permanent ban.
    /// </summary>
    private const string CancellationBanType = "yellow";

    private readonly IBanServiceClient _ban;

    public BanServiceJeeberRestrictionStore(IBanServiceClient ban)
    {
        _ban = ban;
    }

    public async Task<bool> IsRestrictedAsync(string jeeberId, DateTimeOffset at, CancellationToken ct)
    {
        var statuses = await _ban.GetStatusAsync(jeeberId, ct);
        if (!statuses.IsCurrentlyBanned)
        {
            return false;
        }

        // Honour `at`: a time-boxed ban whose expiry has already passed (but
        // ban-service has not yet swept) is no longer a restriction.
        var expiry = statuses.ActiveExpiry;
        if (expiry is { } e)
        {
            return at < e;
        }

        // is_currently_banned with no expiry == permanent BAN: always restricted.
        return true;
    }

    public async Task<DateTimeOffset?> GetActiveExpiryAsync(string jeeberId, DateTimeOffset at, CancellationToken ct)
    {
        var statuses = await _ban.GetStatusAsync(jeeberId, ct);
        if (!statuses.IsCurrentlyBanned)
        {
            return null;
        }

        var expiry = statuses.ActiveExpiry;
        if (expiry is { } e && at < e)
        {
            return e;
        }

        // Banned but no future-dated expiry (permanent, or expiry already
        // passed): no active time-boxed expiry to report.
        return null;
    }

    public async Task ApplyAsync(string jeeberId, DateTimeOffset at, TimeSpan duration, CancellationToken ct)
    {
        // Advance the yellow progression. ban-service owns the resulting
        // banned_until from its stage rules; `duration` is advisory.
        await _ban.ApplyBanAsync(jeeberId, CancellationBanType, ct);
    }
}
