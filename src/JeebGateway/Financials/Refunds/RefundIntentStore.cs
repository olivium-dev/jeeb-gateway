using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Financials.Holds;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials.Refunds;

/// <summary>OD-P1 / invariant I2 — the durable record written BEFORE the compensating credit, so no
/// refund can be owed, attempted or half-finished without the sweeper being able to find it.</summary>
public sealed record RefundIntent(
    string RequestId,
    string JeeberId,
    decimal Amount,
    string CancelledBy,
    DateTimeOffset CreatedAtUtc,
    Guid? TxId,
    string State);

/// <summary>The three states a refund intent carries. <c>closed</c> is a tombstone and reads as
/// ABSENT everywhere (the state-service KV has no delete).</summary>
public static class RefundIntentState
{
    /// <summary>Owed and not yet proven credited — the sweeper re-drives it every pass.</summary>
    public const string Open = "open";

    /// <summary>Same refund key, different money: REPORTED every sweep, never blind-retried.</summary>
    public const string Conflict = "conflict";

    public const string Closed = "closed";
}

public interface IRefundIntentStore
{
    /// <summary>REQUIRED write, never fire-and-forget: a failure THROWS so the caller can count and
    /// log it instead of crediting money nothing can reconcile.</summary>
    Task WriteAsync(RefundIntent intent, CancellationToken ct);

    /// <summary>Latest state of the request's refund intent; null when absent or tombstoned.</summary>
    Task<RefundIntent?> GetAsync(string requestId, CancellationToken ct);

    /// <summary>The sweeper's enumeration surface — every open/conflict intent, tombstones dropped.</summary>
    Task<IReadOnlyList<RefundIntent>> ListAllAsync(CancellationToken ct);

    /// <summary>Tombstones the record once the refund is credited (or proven already credited).</summary>
    Task CloseAsync(string requestId, CancellationToken ct);
}

/// <summary>Refund intents on jeeb-state-service's opaque KV, keyed <c>wgf:refund:{requestId}</c>;
/// the gateway holds no row itself, so an owed refund stays findable across a bounce.</summary>
/// <remarks>MUTABLE STATE ON AN INSERT-ONCE KV: the KV is <c>ON CONFLICT (key) DO NOTHING</c>, so
/// a key can NEVER be overwritten — mechanics mirror <c>HoldIntentStore</c> exactly.</remarks>
/// <remarks>Each write is therefore an APPEND onto a revision chain <c>wgf:refund:{requestId}#r{N}</c>
/// holding the full snapshot; a read takes the highest revision. All keys keep the frozen prefix.</remarks>
public sealed class RefundIntentStore : IRefundIntentStore
{
    /// <summary>Frozen namespace (DESIGN §4) — also the sweeper's prefix-scan root.</summary>
    internal const string KeyPrefix = "wgf:refund:";

    /// <summary>Revision suffix. Not <c>:</c>, so it can never be read as part of a request id.</summary>
    internal const string RevisionMarker = "#r";

    /// <summary>Defensive bound on one request's revision chain (base + txId + retries + tombstone).</summary>
    private const int MaxRevisions = 64;

    /// <summary>Margin so a tombstone outlives the clock skew on the rows it supersedes.</summary>
    private const int TombstoneMarginSeconds = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IJeebStateServiceClient? _stateOrNull;
    private readonly HoldOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RefundIntentStore> _log;

    // Optional so the container still validates where jeeb-state-service is unwired; every
    // operation then throws, which the refunder counts and logs rather than silently dropping.
    public RefundIntentStore(
        IOptions<HoldOptions> options,
        TimeProvider time,
        ILogger<RefundIntentStore> log,
        IJeebStateServiceClient? state = null)
    {
        _stateOrNull = state;
        _options = options.Value;
        _time = time;
        _log = log;
    }

    private IJeebStateServiceClient _state => _stateOrNull ?? throw new InvalidOperationException(
        "refund intents need jeeb-state-service (JeebStateService:Enabled / JeebStateService:BaseUrl); "
        + "it is not wired, so no owed refund can be recorded.");

    public Task WriteAsync(RefundIntent intent, CancellationToken ct)
        => AppendAsync(intent, ClampTtl(_options.IntentTtlSeconds), ct);

    public async Task<RefundIntent?> GetAsync(string requestId, CancellationToken ct)
    {
        var chain = await ReadChainAsync(requestId, ct);
        if (chain.Count == 0) return null;

        var latest = chain[^1].Intent;
        return IsClosed(latest) ? null : latest;
    }

    public async Task<IReadOnlyList<RefundIntent>> ListAllAsync(CancellationToken ct)
    {
        var rows = await _state.FindIdempotencyKeysByPrefixAsync(KeyPrefix, ct);

        var latest = new Dictionary<string, (int Revision, RefundIntent Intent)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var intent = TryParse(row);
            if (intent is null || string.IsNullOrEmpty(intent.RequestId)) continue;

            var revision = RevisionOf(row.Key);
            if (latest.TryGetValue(intent.RequestId, out var seen) && seen.Revision >= revision) continue;
            latest[intent.RequestId] = (revision, intent);
        }

        return latest.Values
            .Where(v => !IsClosed(v.Intent))
            .Select(v => v.Intent)
            .ToList();
    }

    public async Task CloseAsync(string requestId, CancellationToken ct)
    {
        var chain = await ReadChainAsync(requestId, ct);
        var latest = chain.Count == 0 ? null : chain[^1].Intent;
        if (latest is not null && IsClosed(latest)) return;

        var tombstone = latest is null
            ? new RefundIntent(
                requestId, string.Empty, 0m, string.Empty, _time.GetUtcNow(), null, RefundIntentState.Closed)
            : latest with { State = RefundIntentState.Closed };

        await AppendAsync(tombstone, TombstoneTtlSeconds(chain), ct);
    }

    /// <summary>Writes the snapshot at the next free revision. A conflict means a concurrent
    /// writer took that revision, so the next one is tried rather than the write being lost.</summary>
    private async Task AppendAsync(RefundIntent intent, int ttlSeconds, CancellationToken ct)
    {
        var chain = await ReadChainAsync(intent.RequestId, ct);
        var revision = chain.Count == 0 ? 0 : chain[^1].Revision + 1;

        for (var attempt = 0; attempt < MaxRevisions; attempt++, revision++)
        {
            var upsert = await _state.UpsertIdempotencyKeyWithResultAsync(
                new IdempotencyPutRequest
                {
                    Key = RevisionKey(intent.RequestId, revision),
                    StatusCode = 200,
                    ResponseBody = JsonSerializer.SerializeToElement(intent, Json),
                    TtlSeconds = ttlSeconds,
                },
                ct);

            // Only an explicit "not inserted" is a conflict; an unreadable 2xx body is the
            // documented ambiguous case and the write itself did land.
            if (upsert.ResolveInserted() is not false) return;
        }

        throw new InvalidOperationException(
            $"refund intent '{intent.RequestId}' could not be written: {MaxRevisions} revisions are already taken.");
    }

    private async Task<List<ChainRow>> ReadChainAsync(string requestId, CancellationToken ct)
    {
        var rows = await _state.FindIdempotencyKeysByPrefixAsync(BaseKey(requestId), ct);

        var chain = new List<ChainRow>(rows.Count);
        foreach (var row in rows)
        {
            var intent = TryParse(row);
            // Prefix-scan hygiene: a longer request id sharing this prefix is never mistaken for this one.
            if (intent is null || !string.Equals(intent.RequestId, requestId, StringComparison.Ordinal)) continue;
            chain.Add(new ChainRow(RevisionOf(row.Key), intent, row.ExpiresAt));
        }

        chain.Sort(static (a, b) => a.Revision.CompareTo(b.Revision));
        return chain;
    }

    /// <summary>A tombstone must outlive every revision it supersedes, or the chain's top would
    /// fall back to an open row and re-drive an already-credited refund.</summary>
    private int TombstoneTtlSeconds(IReadOnlyList<ChainRow> chain)
    {
        var now = _time.GetUtcNow();
        var ttl = _options.TombstoneTtlSeconds;

        foreach (var row in chain)
        {
            var remaining = row.ExpiresAt is { } expires
                ? (long)Math.Ceiling((expires - now).TotalSeconds) + TombstoneMarginSeconds
                : _options.IntentTtlSeconds;
            if (remaining > ttl) ttl = ClampTtl(remaining);
        }

        return ttl;
    }

    private RefundIntent? TryParse(IdempotencyRecord? row)
    {
        if (row?.ResponseBody is null) return null;

        try
        {
            var json = row.ResponseBody switch
            {
                JsonElement element => element.GetRawText(),
                string raw => raw,
                _ => JsonSerializer.Serialize(row.ResponseBody, Json),
            };
            return JsonSerializer.Deserialize<RefundIntent>(json, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // An unreadable row can never be acted on; skipping beats guessing at a refund's state.
            _log.LogWarning(ex, "fee.refund.intent.unreadable key={Key}", row.Key);
            return null;
        }
    }

    private static bool IsClosed(RefundIntent intent) =>
        string.Equals(intent.State, RefundIntentState.Closed, StringComparison.Ordinal);

    private static string BaseKey(string requestId) => KeyPrefix + requestId;

    private static string RevisionKey(string requestId, int revision) =>
        revision <= 0
            ? BaseKey(requestId)
            : BaseKey(requestId) + RevisionMarker + revision.ToString(CultureInfo.InvariantCulture);

    private static int RevisionOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 0;

        var marker = key.LastIndexOf(RevisionMarker, StringComparison.Ordinal);
        if (marker < 0) return 0;

        return int.TryParse(
            key[(marker + RevisionMarker.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            ? revision
            : 0;
    }

    private static int ClampTtl(long seconds) =>
        seconds <= 0 ? 0 : (int)Math.Min(seconds, int.MaxValue);

    private readonly record struct ChainRow(int Revision, RefundIntent Intent, DateTimeOffset? ExpiresAt);
}
