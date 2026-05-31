using StackExchange.Redis;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Redis-backed <see cref="IChatTopologyMap"/>. Holds the channel&lt;-&gt;conversation
/// id-map the gateway must resolve on every chat send/history because the GENERIC
/// chat-service exposes no lookup-by-external-id.
///
/// The in-memory map (<see cref="InMemoryChatTopologyMap"/>) lost this state on
/// restart and was not multi-replica safe: two gateway replicas would each create
/// their OWN generic member/channel/session for the same Jeeb pair, splitting a
/// conversation across two channels. Backing it with Redis (native
/// <c>192.168.2.50:6379</c>, configured only in appsettings.Production.json via
/// <c>Redis:ConnectionString</c>) makes the map durable and shared. The gateway
/// chooses this impl by config presence — see
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/> — and falls back to
/// the in-memory map when <c>Redis:ConnectionString</c> is absent (dev/test).
///
/// Layout (all under the <c>jeeb:chat:topo:</c> prefix):
///   - HASH  members           field=userId            value=memberId
///   - HASH  members:reverse    field=memberId          value=userId
///   - HASH  channels           field=pairKey           value=channelId
///   - HASH  sessions           field="pairKey|userId"  value=sessionId
///
/// Create-on-miss uses a Redis lock (SET NX with TTL) per logical key so two
/// replicas missing the same key concurrently do not both create the upstream
/// member/channel/session. The lock is best-effort: if it cannot be acquired the
/// caller spin-waits briefly for the winner to publish, then re-reads — at worst a
/// duplicate upstream create, never a torn map.
/// </summary>
public sealed class RedisChatTopologyMap : IChatTopologyMap
{
    private const string Prefix = "jeeb:chat:topo:";
    private static readonly RedisKey MembersKey = Prefix + "members";
    private static readonly RedisKey MembersReverseKey = Prefix + "members:reverse";
    private static readonly RedisKey ChannelsKey = Prefix + "channels";
    private static readonly RedisKey SessionsKey = Prefix + "sessions";

    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(50);
    private const int LockMaxWaitAttempts = 200; // 200 * 50ms = 10s ceiling

    private readonly IConnectionMultiplexer _redis;

    public RedisChatTopologyMap(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public Task<string> GetOrAddMemberAsync(string userId, Func<Task<string>> factory) =>
        GetOrAddAsync(MembersKey, userId, factory, async memberId =>
        {
            // Keep the reverse index in step so transcripts can attribute senders.
            await Db.HashSetAsync(MembersReverseKey, memberId, userId).ConfigureAwait(false);
        });

    public Task<string> GetOrAddChannelAsync(string pairKey, Func<Task<string>> factory) =>
        GetOrAddAsync(ChannelsKey, pairKey, factory, null);

    public Task<string> GetOrAddSessionAsync(string pairKey, string userId, Func<Task<string>> factory) =>
        GetOrAddAsync(SessionsKey, $"{pairKey}|{userId}", factory, null);

    public bool TryGetChannel(string pairKey, out string channelId)
    {
        var v = Db.HashGet(ChannelsKey, pairKey);
        if (v.HasValue)
        {
            channelId = v!;
            return true;
        }
        channelId = string.Empty;
        return false;
    }

    public string GetMemberOrDefault(string userId)
    {
        var v = Db.HashGet(MembersKey, userId);
        return v.HasValue ? v! : string.Empty;
    }

    public bool TryResolveUserByMember(string memberId, out string userId)
    {
        if (!string.IsNullOrEmpty(memberId))
        {
            var v = Db.HashGet(MembersReverseKey, memberId);
            if (v.HasValue)
            {
                userId = v!;
                return true;
            }
        }
        userId = string.Empty;
        return false;
    }

    private IDatabase Db => _redis.GetDatabase();

    /// <summary>
    /// Read-through with a Redis distributed lock around the create-on-miss so
    /// concurrent replicas don't double-create the upstream identity.
    /// </summary>
    private async Task<string> GetOrAddAsync(
        RedisKey hash,
        string field,
        Func<Task<string>> factory,
        Func<string, Task>? afterCreate)
    {
        var db = Db;

        var existing = await db.HashGetAsync(hash, field).ConfigureAwait(false);
        if (existing.HasValue) return existing!;

        var lockKey = (RedisKey)($"{Prefix}lock:{(string)hash!}:{field}");
        var lockToken = Guid.NewGuid().ToString("N");

        var haveLock = await db.StringSetAsync(lockKey, lockToken, LockTtl, When.NotExists)
            .ConfigureAwait(false);

        if (!haveLock)
        {
            // Another replica is creating this key. Spin-wait for it to publish,
            // re-reading the hash. Bounded so a crashed holder (TTL) can't hang us.
            for (var i = 0; i < LockMaxWaitAttempts; i++)
            {
                await Task.Delay(LockPoll).ConfigureAwait(false);
                existing = await db.HashGetAsync(hash, field).ConfigureAwait(false);
                if (existing.HasValue) return existing!;
            }
            // Holder never published (crashed before TTL expiry handoff). Fall
            // through and create ourselves rather than fail the request.
        }

        try
        {
            // Double-check under the lock.
            existing = await db.HashGetAsync(hash, field).ConfigureAwait(false);
            if (existing.HasValue) return existing!;

            var created = await factory().ConfigureAwait(false);
            await db.HashSetAsync(hash, field, created).ConfigureAwait(false);
            if (afterCreate is not null) await afterCreate(created).ConfigureAwait(false);
            return created;
        }
        finally
        {
            if (haveLock)
            {
                // Release only if we still hold it (token match) — avoid deleting
                // a lock a later holder acquired after our TTL lapsed.
                await ReleaseLockAsync(db, lockKey, lockToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReleaseLockAsync(IDatabase db, RedisKey lockKey, string token)
    {
        const string releaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
        try
        {
            await db.ScriptEvaluateAsync(releaseScript, new[] { lockKey }, new RedisValue[] { token })
                .ConfigureAwait(false);
        }
        catch
        {
            // Lock release is best-effort; TTL guarantees eventual expiry.
        }
    }
}
