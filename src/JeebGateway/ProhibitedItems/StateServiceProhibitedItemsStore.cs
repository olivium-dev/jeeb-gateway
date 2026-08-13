using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// gwdbx W3-03 — freeze-import-flip read seam for the lexicon, behind
/// <c>FeatureFlags:ProhibitedItemsMode</c>.
///
/// <para><b>No dual-write.</b> This is a catalog leg: every write goes to the authoritative local
/// store at every rung, and the upstream copy is produced by the one-shot
/// <see cref="JeebGateway.StateService.Config.StateServiceConfigImporter"/> during the W3-10
/// authoring freeze. A dual-write would let the two catalogs diverge with no reconciler.</para>
///
/// <para><b>At "local" (this PR) the upstream is never called at all</b> — the create-time
/// moderation gate takes no new dependency. From <c>dual-write-upstream-read</c> up the published
/// config surface serves <see cref="ListActiveAsync"/>, and any upstream fault or empty envelope
/// FAILS OPEN back to the local store: a state-service blip must never block request creation.</para>
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

    private bool UpstreamReads =>
        GwdbxMigrationOptions.RequiresUpstream(_mode.CurrentValue.ProhibitedItems);

    public async Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
    {
        if (!UpstreamReads)
        {
            return await _inner.ListActiveAsync(ct);
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ReadBudget);
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IStateConfigClient>();

            var surface = await client.GetSurfaceAsync(
                Application, ProhibitedItemsEnvelope.SurfaceKey, budget.Token);
            var items = surface?.Published is { } published
                ? ProhibitedItemsEnvelope.ReadActive(published.Data)
                : Array.Empty<ProhibitedItem>();

            // An empty published surface is indistinguishable from a missed import, and an empty
            // lexicon is a 503 at the gate — so it fails open exactly like a fault.
            if (items.Count > 0)
            {
                return items;
            }

            _log.LogWarning(
                "prohibited-items: config surface {SurfaceKey} published no active items; " +
                "falling back to the local lexicon.",
                ProhibitedItemsEnvelope.SurfaceKey);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "prohibited-items: reading config surface {SurfaceKey} failed; falling back to the " +
                "local lexicon so the create-time moderation gate keeps serving.",
                ProhibitedItemsEnvelope.SurfaceKey);
        }

        return await _inner.ListActiveAsync(ct);
    }

    // Catalog + ack writes and every other read stay local at every rung — freeze-import-flip.
    public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct) =>
        _inner.ListAllAsync(page, pageSize, ct);

    public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct) => _inner.GetAsync(id, ct);

    public Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct) =>
        _inner.CreateAsync(input, adminUserId, ct);

    public Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct) =>
        _inner.UpdateAsync(id, patch, adminUserId, ct);

    public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct) =>
        _inner.GetAcknowledgmentAsync(userId, ct);

    public Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct) =>
        _inner.AcknowledgeAsync(userId, version, ct);

    public Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(int page, int pageSize, CancellationToken ct) =>
        _inner.ListAcknowledgmentsAsync(page, pageSize, ct);
}
