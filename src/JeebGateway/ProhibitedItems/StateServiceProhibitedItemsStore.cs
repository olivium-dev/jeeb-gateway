using System.Text.Json;
using JeebGateway.Infrastructure;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// gwdbx W3-03 — freeze-import-flip read seam for the lexicon AND its ack ledger, behind
/// <c>FeatureFlags:ProhibitedItemsMode</c>.
///
/// <para><b>No catalog dual-write.</b> The upstream copy was produced once by the W3-07
/// freeze-import (retired at ADR-0010); two independently writable catalogs with no reconciler
/// diverge silently. From the read rung up the catalog is therefore UPSTREAM-OWNED and local
/// authoring FAILS CLOSED rather than writing rows the gate will never read.</para>
///
/// <para><b>Reads.</b> At "local" the upstream is never called. From
/// <c>dual-write-upstream-read</c> up the published config surface serves the active catalog, the
/// admin catalog list AND the per-user acknowledgements — W3-11 originally flipped only the first,
/// which left admins looking at an empty local catalog while the gate enforced the upstream one.</para>
///
/// <para><b>Fail-open, honestly.</b> The active-catalog read still fails OPEN so a state-service
/// blip cannot block request creation, but it may never fall open onto a store that is empty BY
/// DESIGN (post-flip the seeder self-skips, so the local lexicon holds nothing and an empty catalog
/// is a fail-CLOSED 503 at <see cref="ModerationGate"/>). Order: upstream, then the last-known-good
/// published snapshot, then the local lexicon only if it actually holds terms. Ack reads and the
/// admin catalog list fail CLOSED instead: answering them from an empty local store would report a
/// confident, wrong zero.</para>
/// </summary>
public sealed class StateServiceProhibitedItemsStore : IProhibitedItemsStore
{
    // Application scope the state-service credential grants this gateway.
    public const string Application = "jeeb-gateway";

    private static readonly TimeSpan ReadBudget = TimeSpan.FromMilliseconds(2000);

    private readonly IProhibitedItemsStore _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<StateServiceProhibitedItemsStore> _log;

    // Last published catalog that actually carried terms. This is what restores the documented
    // fail-OPEN once the local lexicon is empty by design.
    private volatile IReadOnlyList<ProhibitedItem>? _lastKnownGood;

    public StateServiceProhibitedItemsStore(
        IProhibitedItemsStore inner,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<StateServiceProhibitedItemsStore> log)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _mode = mode;
        _log = log;
    }

    /// <summary>The authoritative gateway-local chain this decorator wraps; asserted by tests.</summary>
    internal IProhibitedItemsStore Inner => _inner;

    private GwdbxMigrationPhase Phase => _mode.CurrentValue.ProhibitedItems;

    private bool UpstreamReads => GwdbxMigrationOptions.RequiresUpstream(Phase);

    // Acks are per-user records, not a catalog, so they DO carry a dual-write rung.
    private bool UpstreamAcks => Phase >= GwdbxMigrationPhase.DualWriteLocalRead;

    public async Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return await _inner.ListActiveAsync(ct);
        }

        try
        {
            var published = await ReadPublishedAsync(ct);
            var items = published is null
                ? Array.Empty<ProhibitedItem>()
                : ProhibitedItemsEnvelope.ReadActive(published.Value);

            // An empty published surface is indistinguishable from a missed import, and an empty
            // lexicon is a 503 at the gate — so it degrades exactly like a fault.
            if (items.Count > 0)
            {
                _lastKnownGood = items;
                return items;
            }

            _log.LogWarning(
                "prohibited-items: config surface {SurfaceKey} published no active items.",
                ProhibitedItemsEnvelope.SurfaceKey);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "prohibited-items: reading config surface {SurfaceKey} failed.",
                ProhibitedItemsEnvelope.SurfaceKey);
        }

        var cached = _lastKnownGood;
        if (cached is { Count: > 0 })
        {
            _log.LogWarning(
                "prohibited-items: serving the last-known-good published lexicon ({Count} item(s)) " +
                "so the create-time moderation gate keeps serving.",
                cached.Count);
            return cached;
        }

        var local = await _inner.ListActiveAsync(ct);
        if (local.Count > 0)
        {
            _log.LogWarning(
                "prohibited-items: falling back to the local lexicon ({Count} item(s)).", local.Count);
            return local;
        }

        // Post-flip the local lexicon is empty BY DESIGN. Naming the real cause beats returning an
        // empty catalog that ModerationGate reports as "lexicon could not be loaded".
        throw new OwnerCapabilityUnavailableException(
            "jeeb-state-service published moderation lexicon (no cached snapshot, local lexicon empty)");
    }

    public async Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return await _inner.ListAllAsync(page, pageSize, ct);
        }

        // FAIL CLOSED: the admin catalog must show what the gate enforces. A local fallback here
        // is what made GET /admin/prohibited-items report 0 items against a live 15-item lexicon.
        var all = await ReadAllOrThrowAsync(ct);
        var slice = all.Skip(Math.Max(0, (page - 1) * pageSize)).Take(pageSize).ToArray();
        return new ProhibitedItemsPage { Items = slice, Total = all.Count };
    }

    public async Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return await _inner.GetAsync(id, ct);
        }

        var all = await ReadAllOrThrowAsync(ct);
        return all.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
    }

    public Task<ProhibitedItem> CreateAsync(
        ProhibitedItemCreate input, string adminUserId, CancellationToken ct) =>
        UpstreamReads
            ? Task.FromException<ProhibitedItem>(new OwnerCapabilityUnavailableException(CatalogAuthoring))
            : _inner.CreateAsync(input, adminUserId, ct);

    public Task<ProhibitedItem?> UpdateAsync(
        string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct) =>
        UpstreamReads
            ? Task.FromException<ProhibitedItem?>(new OwnerCapabilityUnavailableException(CatalogAuthoring))
            : _inner.UpdateAsync(id, patch, adminUserId, ct);

    public async Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return await _inner.GetAcknowledgmentAsync(userId, ct);
        }

        // FAIL CLOSED on a fault. The local ack ledger is in-memory and empty after every bounce,
        // so answering from it would read as "never acknowledged" and hide the outage.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ReadBudget);
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IStateConfigClient>();

        var ack = await client.GetAckAsync(
            Application, userId, ProhibitedItemsEnvelope.SurfaceKey, budget.Token);
        return ack is null
            ? null
            : new UserAcknowledgment
            {
                UserId = userId,
                Version = ack.Version,
                AcknowledgedAt = ack.AckedAt,
            };
    }

    public async Task<UserAcknowledgment> AcknowledgeAsync(
        string userId, string version, CancellationToken ct)
    {
        var local = await _inner.AcknowledgeAsync(userId, version, ct);
        if (!UpstreamAcks)
        {
            return local;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ReadBudget);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateConfigClient>();

            await client.UpsertAckAsync(
                userId,
                ProhibitedItemsEnvelope.SurfaceKey,
                new ConfigAckUpsertRequestV1
                {
                    Application = Application,
                    Version = version,
                    AckedAt = local.AcknowledgedAt,
                },
                budget.Token);
        }
        catch (Exception ex)
        {
            // From the read rung up the ack comes back OUT of upstream, so a swallowed write would
            // confirm an acknowledgement the gate can never see.
            if (UpstreamReads) throw;

            _log.LogWarning(ex,
                "prohibited-items acks: upstream upsert failed for {UserId}; the local ledger is " +
                "authoritative at this rung and the acknowledgement stands.",
                userId);
        }

        return local;
    }

    public Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(
        int page, int pageSize, CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return _inner.ListAcknowledgmentsAsync(page, pageSize, ct);
        }

        // state-service exposes acks per SUBJECT only. Enumerating the local (empty) ledger here
        // would hand the parity check and any admin surface a confident, false zero.
        return Task.FromException<UserAcknowledgmentPage>(
            new OwnerCapabilityUnavailableException("jeeb-state-service ack-ledger enumeration"));
    }

    private const string CatalogAuthoring =
        "jeeb-state-service moderation-lexicon authoring (freeze-import-flip: from the read rung up " +
        "the published config surface owns the catalog, so the gateway has no local write to make)";

    private async Task<IReadOnlyList<ProhibitedItem>> ReadAllOrThrowAsync(CancellationToken ct)
    {
        var published = await ReadPublishedAsync(ct);
        if (published is null)
        {
            throw new OwnerCapabilityUnavailableException(
                "jeeb-state-service published moderation lexicon");
        }

        // ADR-0010: any successful published read warms the create-time gate's cache, so an admin
        // opening the catalog no longer leaves ListActiveAsync cold across a later blip.
        var active = ProhibitedItemsEnvelope.ReadActive(published.Value);
        if (active.Count > 0)
        {
            _lastKnownGood = active;
        }

        return ProhibitedItemsEnvelope.ReadAll(published.Value);
    }

    private async Task<JsonElement?> ReadPublishedAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ReadBudget);
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IStateConfigClient>();

        var surface = await client.GetSurfaceAsync(
            Application, ProhibitedItemsEnvelope.SurfaceKey, budget.Token);
        return surface?.Published is { } published ? published.Data : null;
    }
}
