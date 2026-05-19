using System.Globalization;

namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// MVP in-memory <see cref="ICancellationLogStore"/>. Append-only list
/// guarded by a single write lock; reads enumerate a snapshot taken
/// inside the lock so a concurrent <see cref="RecordAsync"/> can't tear
/// the count.
///
/// The ISO-8601 week math uses <see cref="ISOWeek"/> (week starts Monday)
/// so the boundary matches the Q-OPEN-2 "resets Monday" contract the
/// 429 retryAfter advertises.
/// </summary>
public sealed class InMemoryCancellationLogStore : ICancellationLogStore
{
    private readonly List<CancellationLogEntry> _rows = new();
    private readonly object _lock = new();

    public Task RecordAsync(CancellationLogEntry entry, CancellationToken ct)
    {
        lock (_lock)
        {
            _rows.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task<int> CountClientCancellationsInWeekAsync(
        string userId, DateTimeOffset at, CancellationToken ct)
    {
        var (start, end) = IsoWeekBoundsUtc(at);

        int count;
        lock (_lock)
        {
            count = _rows.Count(r =>
                string.Equals(r.UserId, userId, StringComparison.Ordinal)
                && string.Equals(r.Role, CancellationRoles.Client, StringComparison.Ordinal)
                && r.At >= start && r.At < end);
        }
        return Task.FromResult(count);
    }

    public Task<int> CountJeeberStrikesInWindowAsync(
        string userId, DateTimeOffset at, TimeSpan window, CancellationToken ct)
    {
        var since = at - window;
        int count;
        lock (_lock)
        {
            count = _rows.Count(r =>
                string.Equals(r.UserId, userId, StringComparison.Ordinal)
                && string.Equals(r.Role, CancellationRoles.Jeeber, StringComparison.Ordinal)
                && r.StrikeIssued
                && r.At > since && r.At <= at);
        }
        return Task.FromResult(count);
    }

    /// <summary>
    /// Returns the UTC ISO-week boundaries containing <paramref name="at"/>.
    /// Monday 00:00:00 UTC inclusive → next Monday 00:00:00 UTC exclusive.
    /// </summary>
    internal static (DateTimeOffset Start, DateTimeOffset End) IsoWeekBoundsUtc(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        var date = utc.Date;
        // DayOfWeek treats Sunday=0; convert to ISO (Monday=1..Sunday=7).
        var isoDow = ((int)date.DayOfWeek + 6) % 7; // Monday=0..Sunday=6
        var monday = date.AddDays(-isoDow);
        var start = new DateTimeOffset(monday, TimeSpan.Zero);
        var end = start.AddDays(7);
        return (start, end);
    }
}

/// <summary>
/// Canonical role names for the cancellation log. Distinct from
/// <see cref="JeebGateway.Users.Roles"/> because the gateway log records
/// the cancellation actor (client/jeeber) — not the persisted JWT role
/// claim values (customer/driver). Keeping them separate prevents a
/// rename of <see cref="JeebGateway.Users.Roles"/> from silently
/// corrupting the historical cancellation counts.
/// </summary>
internal static class CancellationRoles
{
    public const string Client = "client";
    public const string Jeeber = "jeeber";
}
