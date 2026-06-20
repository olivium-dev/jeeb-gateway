using JeebGateway.ProhibitedItems.Scanner;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// WS-06: the single source of truth for "what does the prohibited-items lexicon say
/// about this text?". Both the create-time request gate (RequestsController) and the
/// standalone content-check endpoint (<c>POST /moderation/jeeb/check</c>, used by ACCT-03
/// display-name moderation and RAT-04) route through here so the fail-closed semantics
/// (empty / unloadable lexicon ⇒ 503) and the block/warn mapping are identical across
/// every caller and cannot drift.
/// </summary>
public sealed class ModerationGate
{
    private readonly IProhibitedItemsStore _store;
    private readonly IProhibitedItemScanner _scanner;

    public ModerationGate(IProhibitedItemsStore store, IProhibitedItemScanner scanner)
    {
        _store = store;
        _scanner = scanner;
    }

    /// <summary>
    /// Scans <paramref name="text"/> against the active lexicon. Throws
    /// <see cref="LexiconUnavailableException"/> when the lexicon cannot be loaded or is
    /// empty so the caller fails CLOSED (503) instead of silently allowing the text through.
    /// </summary>
    public async Task<ModerationGateOutcome> EvaluateAsync(string? text, CancellationToken ct)
    {
        IReadOnlyList<ProhibitedItem> activeItems;
        try
        {
            activeItems = await _store.ListActiveAsync(ct);
        }
        catch (Exception ex)
        {
            throw new LexiconUnavailableException("Prohibited items lexicon could not be loaded.", ex);
        }

        if (activeItems.Count == 0)
        {
            throw new LexiconUnavailableException("Prohibited items lexicon could not be loaded.");
        }

        var version = ComputeLexiconVersion(activeItems);
        var scan = await _scanner.ScanAsync(text, ct);
        return new ModerationGateOutcome(scan.GatingSeverity, scan, version);
    }

    /// <summary>
    /// Lexicon version = max UpdatedAt across the active set. MUST match
    /// <c>ProhibitedItemsController.ComputeVersion</c> so an ack recorded against the
    /// GET /prohibited-items version clears the warn gate everywhere.
    /// </summary>
    public static string ComputeLexiconVersion(IReadOnlyList<ProhibitedItem> items)
    {
        if (items.Count == 0) return "empty";
        return items.Max(i => i.UpdatedAt).ToUniversalTime().ToString("O");
    }
}

/// <summary>WS-06: result of a <see cref="ModerationGate"/> evaluation.</summary>
public sealed record ModerationGateOutcome(
    ProhibitedSeverity? GatingSeverity,
    ProhibitedItemScanResult Scan,
    string Version);

/// <summary>
/// WS-06 fail-closed signal: raised when the prohibited-items lexicon cannot be loaded or
/// is empty while the moderation gate is enabled. Callers translate this to a 503 so the
/// request is rejected rather than allowed past an unavailable moderation service.
/// </summary>
public sealed class LexiconUnavailableException : Exception
{
    public LexiconUnavailableException(string message) : base(message) { }
    public LexiconUnavailableException(string message, Exception inner) : base(message, inner) { }
}
