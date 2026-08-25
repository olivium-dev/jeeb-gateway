using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials.Holds;

/// <summary>DECISION Op 1 / invariant I2 — the durable record written BEFORE a hold is placed,
/// so no placed hold can exist that the sweeper cannot find.</summary>
public sealed record HoldIntent(
    string OfferId,
    string JeeberId,
    string RequestId,
    int Seq,
    decimal ExpectedAmount,
    DateTimeOffset PlacedAtUtc,
    Guid? TxId,
    string State);

/// <summary>The three states an intent carries. <c>closed</c> is a tombstone and reads as
/// ABSENT everywhere (the state-service KV has no delete).</summary>
public static class HoldIntentState
{
    public const string Open = "open";

    /// <summary>Placement was attempted and refused — nothing is held; the sweeper collects it.</summary>
    public const string Failed = "failed";

    public const string Closed = "closed";
}

public interface IHoldIntentStore
{
    /// <summary>REQUIRED write, never fire-and-forget: a failure THROWS so the caller can fail
    /// the transition closed (E5) instead of placing an untrackable hold.</summary>
    Task WriteAsync(HoldIntent intent, CancellationToken ct);

    /// <summary>Latest state of the offer's intent; null when absent or tombstoned.</summary>
    Task<HoldIntent?> GetAsync(string offerId, CancellationToken ct);

    /// <summary>The sweeper's enumeration surface — every open/failed intent, tombstones dropped.</summary>
    Task<IReadOnlyList<HoldIntent>> ListAllAsync(CancellationToken ct);

    /// <summary>Tombstones the record once its hold set is released.</summary>
    Task CloseAsync(string offerId, CancellationToken ct);
}

/// <summary>Intent records on jeeb-state-service's opaque KV, keyed <c>wgf:hold:{offerId}</c>;
/// the gateway holds no row itself, so every hold stays findable across a bounce.</summary>
/// <remarks>MUTABLE STATE ON AN INSERT-ONCE KV: the KV is <c>ON CONFLICT (key) DO NOTHING</c>, so
/// a key can NEVER be overwritten (as documented on <c>StateServiceDisputeCaseStore</c>).</remarks>
/// <remarks>Each write is therefore an APPEND onto a revision chain <c>wgf:hold:{offerId}#r{N}</c>
/// holding the full snapshot; a read takes the highest revision. All keys keep the frozen prefix.</remarks>
public sealed class HoldIntentStore : IHoldIntentStore
{
    /// <summary>Frozen namespace (DECISION, Naming) — also the sweeper's prefix-scan root.</summary>
    internal const string KeyPrefix = "wgf:hold:";

    /// <summary>Revision suffix. Not <c>:</c>, so it can never be read as part of an offer id.</summary>
    internal const string RevisionMarker = "#r";

    /// <summary>Defensive bound on one offer's revision chain (base + txId + raises + tombstone).</summary>
    private const int MaxRevisions = 64;

    /// <summary>Margin so a tombstone outlives the clock skew on the rows it supersedes.</summary>
    private const int TombstoneMarginSeconds = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IJeebStateServiceClient? _stateOrNull;
    private readonly HoldOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HoldIntentStore> _log;

    // Optional so the container still validates where jeeb-state-service is unwired; every
    // operation then throws, which HoldManager maps to E5 rather than an untracked hold.
    public HoldIntentStore(
        IOptions<HoldOptions> options,
        TimeProvider time,
        ILogger<HoldIntentStore> log,
        IJeebStateServiceClient? state = null)
    {
        _stateOrNull = state;
        _options = options.Value;
        _time = time;
        _log = log;
    }

    private IJeebStateServiceClient _state => _stateOrNull ?? throw new InvalidOperationException(
        "hold intents need jeeb-state-service (JeebStateService:Enabled / JeebStateService:BaseUrl); "
        + "it is not wired, so no hold can be recorded and every placement fails closed.");

    public Task WriteAsync(HoldIntent intent, CancellationToken ct)
        => AppendAsync(intent, ClampTtl(_options.IntentTtlSeconds), ct);

    public async Task<HoldIntent?> GetAsync(string offerId, CancellationToken ct)
    {
        var chain = await ReadChainAsync(offerId, ct);
        if (chain.Count == 0) return null;

        var latest = chain[^1].Intent;
        return IsClosed(latest) ? null : latest;
    }

    public async Task<IReadOnlyList<HoldIntent>> ListAllAsync(CancellationToken ct)
    {
        var rows = await _state.FindIdempotencyKeysByPrefixAsync(KeyPrefix, ct);

        var latest = new Dictionary<string, (int Revision, HoldIntent Intent)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var intent = TryParse(row);
            if (intent is null || string.IsNullOrEmpty(intent.OfferId)) continue;

            var revision = RevisionOf(row.Key);
            if (latest.TryGetValue(intent.OfferId, out var seen) && seen.Revision >= revision) continue;
            latest[intent.OfferId] = (revision, intent);
        }

        return latest.Values
            .Where(v => !IsClosed(v.Intent))
            .Select(v => v.Intent)
            .ToList();
    }

    public async Task CloseAsync(string offerId, CancellationToken ct)
    {
        var chain = await ReadChainAsync(offerId, ct);
        var latest = chain.Count == 0 ? null : chain[^1].Intent;
        if (latest is not null && IsClosed(latest)) return;

        var tombstone = latest is null
            ? new HoldIntent(offerId, string.Empty, string.Empty, 0, 0m, _time.GetUtcNow(), null, HoldIntentState.Closed)
            : latest with { State = HoldIntentState.Closed };

        await AppendAsync(tombstone, TombstoneTtlSeconds(chain), ct);
    }

    /// <summary>Writes the snapshot at the next free revision. A conflict means a concurrent
    /// writer took that revision, so the next one is tried rather than the write being lost.</summary>
    private async Task AppendAsync(HoldIntent intent, int ttlSeconds, CancellationToken ct)
    {
        var chain = await ReadChainAsync(intent.OfferId, ct);
        var revision = chain.Count == 0 ? 0 : chain[^1].Revision + 1;

        for (var attempt = 0; attempt < MaxRevisions; attempt++, revision++)
        {
            var upsert = await _state.UpsertIdempotencyKeyWithResultAsync(
                new IdempotencyPutRequest
                {
                    Key = RevisionKey(intent.OfferId, revision),
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
            $"hold intent '{intent.OfferId}' could not be written: {MaxRevisions} revisions are already taken.");
    }

    private async Task<List<ChainRow>> ReadChainAsync(string offerId, CancellationToken ct)
    {
        var rows = await _state.FindIdempotencyKeysByPrefixAsync(BaseKey(offerId), ct);

        var chain = new List<ChainRow>(rows.Count);
        foreach (var row in rows)
        {
            var intent = TryParse(row);
            // Prefix-scan hygiene: a longer offer id sharing this prefix is never mistaken for this one.
            if (intent is null || !string.Equals(intent.OfferId, offerId, StringComparison.Ordinal)) continue;
            chain.Add(new ChainRow(RevisionOf(row.Key), intent, row.ExpiresAt));
        }

        chain.Sort(static (a, b) => a.Revision.CompareTo(b.Revision));
        return chain;
    }

    /// <summary>A tombstone must outlive every revision it supersedes, or the chain's top would
    /// fall back to an open row and resurrect a released hold.</summary>
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

    private HoldIntent? TryParse(IdempotencyRecord? row)
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
            return JsonSerializer.Deserialize<HoldIntent>(json, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // An unreadable row can never be acted on; skipping beats guessing at a hold's state.
            _log.LogWarning(ex, "hold.intent.unreadable key={Key}", row.Key);
            return null;
        }
    }

    private static bool IsClosed(HoldIntent intent) =>
        string.Equals(intent.State, HoldIntentState.Closed, StringComparison.Ordinal);

    private static string BaseKey(string offerId) => KeyPrefix + offerId;

    private static string RevisionKey(string offerId, int revision) =>
        revision <= 0
            ? BaseKey(offerId)
            : BaseKey(offerId) + RevisionMarker + revision.ToString(CultureInfo.InvariantCulture);

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

    private readonly record struct ChainRow(int Revision, HoldIntent Intent, DateTimeOffset? ExpiresAt);
}
