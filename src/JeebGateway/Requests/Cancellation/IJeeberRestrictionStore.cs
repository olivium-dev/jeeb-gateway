namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// T-backend-024 (JEEB-42): tracks the 24-hour "no new offers" restriction
/// that fires when a Jeeber accumulates 3 or more cancellations in a
/// rolling 7-day window. The matching layer consults
/// <see cref="IsRestrictedAsync"/> before fanning out new offers to a
/// candidate Jeeber.
///
/// Production swap: a Postgres-backed implementation reading from a
/// <c>jeeber_restrictions</c> table whose rows expire when
/// <c>expires_at &lt; now()</c>; the rolling-7d count is derived from
/// <c>delivery_requests WHERE cancelled_by = 'jeeber' AND cancellation_requested_at &gt; now() - interval '7 days'</c>.
/// </summary>
public interface IJeeberRestrictionStore
{
    /// <summary>
    /// Returns true when <paramref name="jeeberId"/> is currently inside
    /// an active restriction window — i.e. <c>at &lt; expires_at</c>.
    /// </summary>
    Task<bool> IsRestrictedAsync(string jeeberId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Returns the active restriction expiry for <paramref name="jeeberId"/>,
    /// or null when no restriction is in effect.
    /// </summary>
    Task<DateTimeOffset?> GetActiveExpiryAsync(string jeeberId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Applies a new 24-hour restriction starting at <paramref name="at"/>.
    /// Overwrites any earlier restriction so a fresh trigger always extends
    /// the block to a full 24 hours from now (the spec is "trigger 24hr
    /// restriction" — not "extend by 24hr from previous expiry").
    /// </summary>
    Task ApplyAsync(string jeeberId, DateTimeOffset at, TimeSpan duration, CancellationToken ct);
}
