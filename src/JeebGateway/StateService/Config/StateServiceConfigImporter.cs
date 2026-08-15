using JeebGateway.Migration;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.StateService.Config;

/// <summary>
/// gwdbx W3-03 — the freeze-import half of freeze-import-flip (W3-10 freezes authoring, W3-07 runs
/// this, W3-11 flips). Reads the authoritative gateway-LOCAL stores and replays them onto the
/// surfaces the read rung will serve from; every leg is idempotent, paged and re-runnable (G-21).
///
/// <para>There is deliberately NO dual-write decorator on these catalog legs: two independently
/// writable catalogs with no reconciler diverge silently. The import is the only writer upstream
/// until the flip.</para>
///
/// <para>Both sides are resolved EXPLICITLY — <see cref="ILocalProhibitedItemsStore"/> /
/// <see cref="ILocalFlaggedRequestStore"/> for the source, <see cref="IUpstreamFlaggedRequestStore"/>
/// and <see cref="IStateConfigClient"/> for the target. Resolving the serving interfaces would read
/// upstream and re-publish it to itself once the rung is live.</para>
///
/// <para>The CMS leg was DELETED by ADR-0008: bundler-service owns every CMS row, so replaying it
/// into state-service would fork the catalog into exactly the two-writer shape above.</para>
/// </summary>
public sealed class StateServiceConfigImporter
{
    // Application scope the state-service credential grants this gateway.
    public const string Application = "jeeb-gateway";

    // Recorded as the deciding admin when a replayed row was already decided locally.
    public const string ImportActor = "gwdbx-w3-07-import";

    // Bulk-import size caps: a page cap keeps one request small, the page cap bounds a runaway loop.
    private const int PageSize = 200;
    private const int MaxPages = 500;

    private readonly ILocalProhibitedItemsStore _lexicon;
    private readonly ILocalFlaggedRequestStore _flaggedLocal;
    private readonly IUpstreamFlaggedRequestStore _flaggedUpstream;
    private readonly IStateConfigClient _config;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<StateServiceConfigImporter> _log;

    public StateServiceConfigImporter(
        ILocalProhibitedItemsStore lexicon,
        ILocalFlaggedRequestStore flaggedLocal,
        IUpstreamFlaggedRequestStore flaggedUpstream,
        IStateConfigClient config,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<StateServiceConfigImporter> log)
    {
        _lexicon = lexicon;
        _flaggedLocal = flaggedLocal;
        _flaggedUpstream = flaggedUpstream;
        _config = config;
        _mode = mode;
        _log = log;
    }

    // G-15 — publishing the same lexicon version twice is one upstream version, never two.
    public static string LexiconPublishKey(string versionTag) =>
        "config-import:" + ProhibitedItemsEnvelope.SurfaceKey + ":" + versionTag;

    /// <summary>
    /// Runs every leg. <paramref name="force"/> is required once a leg's mode has reached
    /// <c>dual-write-upstream-read</c>: from there the published surface is SERVING reads, and a
    /// re-publish would swap the live lexicon under the create-time gate.
    /// </summary>
    public async Task<ConfigImportReport> ImportAsync(bool force, CancellationToken ct)
    {
        var report = new ConfigImportReport();

        if (Serving(_mode.CurrentValue.ProhibitedItems) && !force)
        {
            report.SkippedLegs.Add("prohibited-items: mode is serving upstream reads; pass force to re-import");
        }
        else
        {
            report.LexiconVersionTag = await ImportLexiconAsync(report, ct);
            report.Acks = await ImportAcksAsync(report.LexiconVersionTag, ct);
            report.FlaggedRequests = await ImportFlaggedAsync(ct);
        }

        _log.LogInformation(
            "gwdbx W3-03 config import: items={Items} acks={Acks} flagged={Flagged} " +
            "lexiconVersion={VersionTag} skipped=[{Skipped}]",
            report.LexiconItems, report.Acks, report.FlaggedRequests,
            report.LexiconVersionTag, string.Join("; ", report.SkippedLegs));
        return report;
    }

    private static bool Serving(GwdbxMigrationPhase phase) =>
        GwdbxMigrationOptions.RequiresUpstream(phase);

    private async Task<string> ImportLexiconAsync(ConfigImportReport report, CancellationToken ct)
    {
        var all = new List<ProhibitedItem>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var slice = await _lexicon.ListAllAsync(page, PageSize, ct);
            all.AddRange(slice.Items);
            if (slice.Items.Count < PageSize || all.Count >= slice.Total) break;
        }
        report.LexiconItems = all.Count;

        // The version tag MUST be the gateway's own lexicon version: acks are recorded against it,
        // so a different token would silently un-acknowledge every user at the flip.
        var active = all.Where(i => i.Active).ToList();
        var versionTag = ModerationGate.ComputeLexiconVersion(active);

        await _config.UpsertDraftAsync(
            ProhibitedItemsEnvelope.SurfaceKey,
            new ConfigDraftUpsertRequestV1
            {
                Application = Application,
                Title = ProhibitedItemsEnvelope.SurfaceTitle,
                Data = ProhibitedItemsEnvelope.Serialize(all),
            },
            ct);

        await _config.PublishAsync(
            ProhibitedItemsEnvelope.SurfaceKey,
            new ConfigPublishRequestV1
            {
                Application = Application,
                PublishedByRef = "gwdbx-w3-03-import",
                VersionTag = versionTag,
            },
            LexiconPublishKey(versionTag),
            ct);

        return versionTag;
    }

    private async Task<int> ImportAcksAsync(string versionTag, CancellationToken ct)
    {
        var imported = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var slice = await _lexicon.ListAcknowledgmentsAsync(page, PageSize, ct);
            foreach (var ack in slice.Items)
            {
                await _config.UpsertAckAsync(
                    ack.UserId,
                    ProhibitedItemsEnvelope.SurfaceKey,
                    new ConfigAckUpsertRequestV1
                    {
                        Application = Application,
                        Version = ack.Version,
                        AckedAt = ack.AcknowledgedAt,
                    },
                    ct);
                imported++;
            }
            if (slice.Items.Count < PageSize || imported >= slice.Total) break;
        }
        return imported;
    }

    // Replays onto the state-service CASE engine — the one surface StateServiceFlaggedRequestStore
    // serves the read rung from. The old work-items leg wrote a kind nothing ever read.
    private async Task<int> ImportFlaggedAsync(CancellationToken ct)
    {
        var imported = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var slice = await _flaggedLocal.ListAsync(null, page, PageSize, ct);
            foreach (var row in slice.Items)
            {
                var created = await _flaggedUpstream.CreateAsync(
                    new FlaggedRequestCreate
                    {
                        RequestId = row.RequestId,
                        UserId = row.UserId,
                        Description = row.Description,
                        Matches = row.Matches,
                    },
                    ct);

                // A decided row replays its decision too, otherwise the flip would re-open every
                // cleared/upheld case. Both keys are content digests, so a re-run no-ops (G-21).
                if (row.Status != FlaggedRequestStatus.Pending)
                {
                    await _flaggedUpstream.DecideAsync(
                        created.Id, row.Status, row.DecidedBy ?? ImportActor, row.DecisionNote, ct);
                }
                imported++;
            }
            if (slice.Items.Count < PageSize || imported >= slice.Total) break;
        }
        return imported;
    }
}

/// <summary>Per-leg counts the W3-07 operator records against the import run.</summary>
public sealed class ConfigImportReport
{
    public int LexiconItems { get; set; }
    public int Acks { get; set; }
    public int FlaggedRequests { get; set; }
    public string LexiconVersionTag { get; set; } = string.Empty;
    public List<string> SkippedLegs { get; } = new();
}
