using System.Data.Common;
using Microsoft.Extensions.Hosting;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// JEB-63 (S05): seeds a minimal default prohibited-items lexicon at startup so
/// the gateway-owned create-time moderation gate has terms to match. Registered
/// whenever <c>FeatureFlags:CreateModeration:Enabled</c> is not explicitly
/// <c>false</c> (default ON), so the moderation gate has a populated lexicon
/// independent of the durable-requests flag. Setting the flag to <c>false</c>
/// skips the seeder and keeps the empty-lexicon behaviour (GET /prohibited-items
/// → items:[], version:'empty').
///
/// Idempotent: skips entirely when the catalog already holds any item, so a
/// gateway restart (or an admin who has already seeded the lexicon out-of-band)
/// does not duplicate entries. The lexicon stays GATEWAY-OWNED — the N11
/// boundary guard requires the prohibited-items lexicon to live only under the
/// gateway list key (<see cref="JeebModerationList.ListKey"/> =
/// <c>jeeb-prohibited-items</c>), never in ban-service.
///
/// Severity mapping mirrors the S05 contract: an alcohol term ("arak") is a
/// <see cref="ProhibitedSeverity.Block"/> hard reject; a bladed-weapon term
/// ("kitchen knife") is a <see cref="ProhibitedSeverity.Warn"/> ack-gate.
/// </summary>
public sealed class DefaultLexiconSeeder : IHostedService
{
    private const string SeedAdmin = "system:lexicon-seed";

    /// <summary>
    /// (name, category, severity). Names match the scanner's exact/word-boundary
    /// matching against a normalized description, so "a bottle of arak" hits
    /// "arak" and "a kitchen knife" hits "kitchen knife".
    /// </summary>
    private static readonly (string Name, string Category, ProhibitedSeverity Severity)[] Defaults =
    {
        ("arak", "alcohol", ProhibitedSeverity.Block),
        ("alcohol", "alcohol", ProhibitedSeverity.Block),
        ("kitchen knife", "weapon", ProhibitedSeverity.Warn),
        ("knife", "weapon", ProhibitedSeverity.Warn),
    };

    private readonly IProhibitedItemsStore _store;
    private readonly ILogger<DefaultLexiconSeeder> _logger;

    public DefaultLexiconSeeder(IProhibitedItemsStore store, ILogger<DefaultLexiconSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Degrade-don't-crash: with a durable (Postgres-backed) IProhibitedItemsStore the
        // startup catalog read can fail transiently (DB briefly unreachable at boot). The
        // seeder must NEVER fault host startup over it — a missing lexicon degrades the
        // create-moderation gate to empty (recoverable on the next restart / admin seed),
        // whereas a thrown StartAsync would crash-loop the whole gateway. With the in-memory
        // store this read never throws, so behaviour there is byte-identical.
        IReadOnlyList<ProhibitedItem> existing;
        try
        {
            existing = await _store.ListActiveAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            // F6: degrade ONLY on a connectivity/transient DB fault — a schema or
            // programming error must surface at boot, not be silently swallowed, and
            // OperationCanceledException (host shutdown) must propagate untouched.
            _logger.LogWarning(ex,
                "Prohibited-items lexicon read failed at startup (transient DB fault); skipping default seed " +
                "(moderation lexicon stays empty until the store is reachable). Host startup continues.");
            return;
        }

        if (existing.Count > 0)
        {
            _logger.LogInformation(
                "Prohibited-items lexicon already has {Count} active item(s); skipping default seed.",
                existing.Count);
            return;
        }

        var seeded = 0;
        foreach (var (name, category, severity) in Defaults)
        {
            try
            {
                await _store.CreateAsync(
                    new ProhibitedItemCreate { Name = name, Category = category, Severity = severity },
                    SeedAdmin,
                    cancellationToken);
                seeded++;
            }
            catch (DuplicateProhibitedItemNameException)
            {
                // Another concurrent seed or an admin entry already covers this
                // name — idempotent, not an error.
            }
            catch (DbException ex)
            {
                // F6: only a connectivity/transient DB fault is a skip-this-entry —
                // a schema/programming fault (and OperationCanceledException) propagates.
                _logger.LogWarning(ex,
                    "Failed to seed prohibited item '{Name}' (transient DB fault); skipping entry.",
                    name);
            }
        }

        _logger.LogInformation(
            "Seeded {Count} default prohibited-items lexicon entries into gateway-owned list '{ListKey}' (create-moderation ON).",
            seeded,
            JeebModerationList.ListKey);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
