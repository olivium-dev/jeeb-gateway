using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.Logging;

namespace JeebGateway.StateService.Config;

// gwdbx W3-07 PREP — read-only parity check between the gateway-local config stores and the
// state-service primitive. Writes NOTHING. The CMS leg was removed by ADR-0008.
public sealed class ConfigParityChecker
{
    private const int PageSize = 200;
    private const int MaxPages = 500;

    private readonly IProhibitedItemsStore _lexicon;
    private readonly IFlaggedRequestStore _flagged;
    private readonly IStateConfigClient _config;
    private readonly IStateOwnershipClient _ownership;
    private readonly ILogger<ConfigParityChecker> _log;

    public ConfigParityChecker(
        IProhibitedItemsStore lexicon,
        IFlaggedRequestStore flagged,
        IStateConfigClient config,
        IStateOwnershipClient ownership,
        ILogger<ConfigParityChecker> log)
    {
        _lexicon = lexicon;
        _flagged = flagged;
        _config = config;
        _ownership = ownership;
        _log = log;
    }

    public async Task<ConfigParityReport> CheckAsync(CancellationToken ct)
    {
        var report = new ConfigParityReport();
        await CheckLexiconAsync(report, ct);
        await CheckAcksAsync(report, ct);
        await CheckFlaggedAsync(report, ct);

        _log.LogInformation(
            "gwdbx W3-07 config parity: clean={Clean} lexicon={LexLocal}/{LexUp} tag={LocalTag}|{UpTag} " +
            "acks={AcksMatched}/{AcksChecked} flagged={FlaggedMatched}/{FlaggedSubjects} " +
            "mismatches={Count}{Truncated}",
            report.Clean, report.LexiconLocalActive, report.LexiconUpstreamActive,
            report.LexiconLocalTag, report.LexiconUpstreamTag,
            report.AcksMatched, report.AcksChecked, report.FlaggedMatched, report.FlaggedSubjects,
            report.Mismatches.Count, report.Truncated ? " (TRUNCATED)" : string.Empty);
        return report;
    }

    private async Task CheckLexiconAsync(ConfigParityReport report, CancellationToken ct)
    {
        var all = new List<ProhibitedItem>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var slice = await _lexicon.ListAllAsync(page, PageSize, ct);
            all.AddRange(slice.Items);
            if (slice.Items.Count < PageSize || all.Count >= slice.Total) break;
        }

        // Version-tag semantics MUST mirror the importer: tag computed over the ACTIVE set only.
        var localActive = Sort(all.Where(i => i.Active));
        report.LexiconLocalActive = localActive.Count;
        report.LexiconLocalTag = ModerationGate.ComputeLexiconVersion(localActive);

        var upstream = await _config.GetSurfaceAsync(
            StateServiceConfigImporter.Application, ProhibitedItemsEnvelope.SurfaceKey, ct);
        if (upstream?.Published is not { } published)
        {
            report.Add("lexicon: no published '" + ProhibitedItemsEnvelope.SurfaceKey + "' surface upstream");
            return;
        }

        report.LexiconUpstreamTag = published.VersionTag;
        if (!string.Equals(published.VersionTag, report.LexiconLocalTag, StringComparison.Ordinal))
        {
            report.Add($"lexicon: version tag differs local='{report.LexiconLocalTag}' upstream='{published.VersionTag}'");
        }

        var upstreamActive = ProhibitedItemsEnvelope.ReadActive(published.Data);
        report.LexiconUpstreamActive = upstreamActive.Count;
        var upstreamById = upstreamActive.ToDictionary(i => i.Id, StringComparer.Ordinal);

        foreach (var local in localActive)
        {
            if (!upstreamById.Remove(local.Id, out var remote))
            {
                report.Add($"lexicon: active item {local.Id} ('{local.Name}') missing upstream");
                continue;
            }
            if (!string.Equals(local.Name, remote.Name, StringComparison.Ordinal)
                || !string.Equals(local.Category, remote.Category, StringComparison.Ordinal)
                || !string.Equals(local.Description, remote.Description, StringComparison.Ordinal)
                || local.Severity != remote.Severity)
            {
                report.Add($"lexicon: item {local.Id} differs (name/category/description/severity)");
            }
        }
        foreach (var orphan in upstreamById.Values)
        {
            report.Add($"lexicon: upstream item {orphan.Id} ('{orphan.Name}') is not active locally");
        }
    }

    private async Task CheckAcksAsync(ConfigParityReport report, CancellationToken ct)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            var slice = await _lexicon.ListAcknowledgmentsAsync(page, PageSize, ct);
            foreach (var ack in slice.Items)
            {
                report.AcksChecked++;
                var remote = await _config.GetAckAsync(
                    StateServiceConfigImporter.Application, ack.UserId,
                    ProhibitedItemsEnvelope.SurfaceKey, ct);
                if (remote is null)
                {
                    report.Add($"acks: user {ack.UserId} has no upstream ack");
                }
                else if (!string.Equals(remote.Version, ack.Version, StringComparison.Ordinal))
                {
                    report.Add($"acks: user {ack.UserId} version differs local='{ack.Version}' upstream='{remote.Version}'");
                }
                else
                {
                    report.AcksMatched++;
                }
            }
            if (slice.Items.Count < PageSize || report.AcksChecked >= slice.Total) break;
        }
    }

    private async Task CheckFlaggedAsync(ConfigParityReport report, CancellationToken ct)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= MaxPages; page++)
        {
            var slice = await _flagged.ListAsync(null, page, PageSize, ct);
            foreach (var row in slice.Items)
            {
                report.FlaggedRows++;
                subjects.Add(row.UserId);
            }
            if (slice.Items.Count < PageSize || report.FlaggedRows >= slice.Total) break;
        }

        // The work-items API exposes only the LATEST item per subject, so this leg proves
        // per-subject existence; per-row parity is the import report's count.
        report.FlaggedSubjects = subjects.Count;
        foreach (var subject in subjects)
        {
            var latest = await _ownership.GetLatestWorkItemAsync(
                StateServiceConfigImporter.Application, StateServiceConfigImporter.FlaggedWorkKind, subject, ct);
            if (latest is null)
            {
                report.Add($"flagged: subject {subject} has no '{StateServiceConfigImporter.FlaggedWorkKind}' work item upstream");
            }
            else
            {
                report.FlaggedMatched++;
            }
        }
    }

    private static List<ProhibitedItem> Sort(IEnumerable<ProhibitedItem> items) =>
        items.OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
             .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
             .ToList();
}

/// <summary>Read-only parity result; Clean gates W3-11, Mismatches capped at 50.</summary>
public sealed class ConfigParityReport
{
    private const int MaxMismatches = 50;

    public int LexiconLocalActive { get; set; }
    public int LexiconUpstreamActive { get; set; }
    public string LexiconLocalTag { get; set; } = string.Empty;
    public string LexiconUpstreamTag { get; set; } = string.Empty;
    public int AcksChecked { get; set; }
    public int AcksMatched { get; set; }
    public int FlaggedRows { get; set; }
    public int FlaggedSubjects { get; set; }
    public int FlaggedMatched { get; set; }
    public bool Truncated { get; private set; }
    public List<string> Mismatches { get; } = new();

    public bool Clean => Mismatches.Count == 0 && !Truncated;

    public void Add(string mismatch)
    {
        if (Mismatches.Count >= MaxMismatches) { Truncated = true; return; }
        Mismatches.Add(mismatch);
    }
}
