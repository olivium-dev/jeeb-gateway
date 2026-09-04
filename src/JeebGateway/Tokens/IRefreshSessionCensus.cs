namespace JeebGateway.Tokens;

/// <summary>What this process knows about live refresh sessions. Feeds the
/// <c>refresh-role-continuity</c> readiness row (D2 §4a).</summary>
public interface IRefreshSessionCensus
{
    /// <summary>Distinct rotating sessions this process has served since boot (keyed by bounded
    /// family id, else user id — a LOWER bound on families). See the implementation for why.</summary>
    int ActiveFamilies { get; }

    /// <summary>Rotations that resolved no roles and were refused (G5 fail-closed).</summary>
    int RolesEmptyRefreshes { get; }

    /// <summary>UTC of the most recent roles-empty rotation, or null.</summary>
    DateTimeOffset? LastRolesEmptyAt { get; }

    /// <summary>Called when a rotation is served for a known refresh family.</summary>
    void RecordRotation(string familyKey);

    /// <summary>Called at the exact point a roles-less token would have been minted.</summary>
    void RecordRolesEmptyRefresh(DateTimeOffset at);
}

/// <summary>
/// In-process census. <b>Why "families this process has served", not "families that exist":</b>
/// the durable refresh store is a key-addressed KV (<see cref="StateServiceRefreshTokenStore"/>)
/// with no enumeration, so no process can count live sessions outright. What it CAN observe is
/// that real sessions are rotating against it — which is exactly the population the empty
/// users-store would harm, and it is observable from the first rotation after a restart.
/// </summary>
public sealed class InProcessRefreshSessionCensus : IRefreshSessionCensus
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _families = new();
    private int _rolesEmpty;
    private long _lastRolesEmptyTicks;

    public int ActiveFamilies => _families.Count;

    public int RolesEmptyRefreshes => Volatile.Read(ref _rolesEmpty);

    public DateTimeOffset? LastRolesEmptyAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRolesEmptyTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void RecordRotation(string familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
        {
            return;
        }

        // Bound the set: this is an alarm input, not an inventory. Past the cap the
        // signal ("sessions are rotating") is already established and cannot regress.
        if (_families.Count < MaxTrackedFamilies)
        {
            _families.TryAdd(familyKey, 0);
        }
    }

    public void RecordRolesEmptyRefresh(DateTimeOffset at)
    {
        Interlocked.Increment(ref _rolesEmpty);
        Interlocked.Exchange(ref _lastRolesEmptyTicks, at.ToUniversalTime().Ticks);
    }

    internal const int MaxTrackedFamilies = 10_000;
}
