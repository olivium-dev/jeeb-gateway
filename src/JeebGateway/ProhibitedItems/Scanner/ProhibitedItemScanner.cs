namespace JeebGateway.ProhibitedItems.Scanner;

/// <summary>
/// Text-matching scanner for the active prohibited-items catalog.
///
/// Pipeline:
///   1. normalize description (lowercase, diacritic strip, punctuation → space)
///   2. tokenize on whitespace
///   3. for each active item, gather candidate surface forms:
///        - the catalog name
///        - registered synonyms (<see cref="IProhibitedItemSynonymRegistry"/>)
///   4. for each surface form:
///        - multi-word form  → word-boundary substring match against normalized text
///        - single-word form → exact token hit, otherwise Damerau-Levenshtein
///          with a length-tiered distance budget
///   5. score, dedupe by (item, type), return matches above the report floor
///
/// Fuzzy thresholds are deliberately tight (length-tiered, short tokens never
/// fuzz) so the false-positive rate stays under the 5% acceptance target;
/// the unit test suite measures this against a curated benign corpus.
///
/// The scanner NEVER auto-blocks. It returns <see cref="ProhibitedItemScanResult.RequiresReview"/>
/// so the caller can route flagged requests to the admin queue.
/// </summary>
public class ProhibitedItemScanner : IProhibitedItemScanner
{
    private readonly IProhibitedItemsStore _store;
    private readonly IProhibitedItemSynonymRegistry _synonyms;

    // Score floors: anything below ReportFloor isn't returned at all;
    // anything between ReportFloor and ReviewThreshold is reported but
    // doesn't trip RequiresReview. The gap exists so callers can show a
    // "we noticed something" hint without forcing an admin moderation row.
    private const double ReportFloor = 0.70;
    private const double ReviewThreshold = 0.78;

    // Tokens shorter than this never participate in fuzzy matching. Picked
    // empirically: at length 3, distance-1 reaches an unacceptable share of
    // unrelated 3-letter words ("ice"/"icy", "gun"/"fun").
    private const int MinFuzzyTokenLength = 4;

    public ProhibitedItemScanner(IProhibitedItemsStore store, IProhibitedItemSynonymRegistry synonyms)
    {
        _store = store;
        _synonyms = synonyms;
    }

    public async Task<ProhibitedItemScanResult> ScanAsync(string? description, CancellationToken ct)
    {
        var normalized = TextNormalizer.Normalize(description);
        if (normalized.Length == 0)
        {
            return new ProhibitedItemScanResult
            {
                Matches = Array.Empty<ProhibitedItemMatch>(),
                RequiresReview = false
            };
        }

        var tokens = TextNormalizer.Tokenize(normalized);
        var items = await _store.ListActiveAsync(ct);

        // Dedupe by (itemId, matchType) keeping the highest confidence so the
        // queue isn't spammed with five "knife"-shaped hits from one sentence.
        var best = new Dictionary<(string itemId, ProhibitedMatchType type), ProhibitedItemMatch>();

        foreach (var item in items)
        {
            EvaluateTerm(item, item.Name, ProhibitedMatchType.Exact, normalized, tokens, best);

            foreach (var synonym in _synonyms.GetSynonyms(item.Name))
            {
                EvaluateTerm(item, synonym, ProhibitedMatchType.Synonym, normalized, tokens, best);
            }
        }

        var matches = best.Values
            .Where(m => m.Confidence >= ReportFloor)
            .OrderByDescending(m => m.Confidence)
            .ThenBy(m => m.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProhibitedItemScanResult
        {
            Matches = matches,
            RequiresReview = matches.Any(m => m.Confidence >= ReviewThreshold)
        };
    }

    private static void EvaluateTerm(
        ProhibitedItem item,
        string rawTerm,
        ProhibitedMatchType originalType,
        string normalizedText,
        IReadOnlyList<string> tokens,
        Dictionary<(string itemId, ProhibitedMatchType type), ProhibitedItemMatch> sink)
    {
        var term = TextNormalizer.Normalize(rawTerm);
        if (term.Length == 0) return;

        var termTokens = TextNormalizer.Tokenize(term);
        if (termTokens.Count == 0) return;

        // Multi-word term: word-boundary substring containment.
        if (termTokens.Count > 1)
        {
            var bordered = " " + normalizedText + " ";
            var needle = " " + term + " ";
            if (bordered.Contains(needle, StringComparison.Ordinal))
            {
                Record(sink, new ProhibitedItemMatch
                {
                    ItemId = item.Id,
                    ItemName = item.Name,
                    Category = item.Category,
                    MatchedTerm = rawTerm,
                    Evidence = term,
                    MatchType = originalType,
                    Confidence = 1.0
                });
            }
            return;
        }

        var single = termTokens[0];

        // Pass 1: exact token hit.
        if (tokens.Contains(single, StringComparer.Ordinal))
        {
            Record(sink, new ProhibitedItemMatch
            {
                ItemId = item.Id,
                ItemName = item.Name,
                Category = item.Category,
                MatchedTerm = rawTerm,
                Evidence = single,
                MatchType = originalType,
                Confidence = 1.0
            });
            return;
        }

        // Pass 2: length-tiered fuzzy. Short terms skip fuzz entirely.
        if (single.Length < MinFuzzyTokenLength) return;
        var budget = FuzzyBudget(single.Length);
        if (budget == 0) return;

        ProhibitedItemMatch? bestFuzzy = null;
        foreach (var tok in tokens)
        {
            // Length-difference prune before the matrix.
            if (Math.Abs(tok.Length - single.Length) > budget) continue;

            var d = DamerauLevenshtein.Distance(single, tok, budget);
            if (d > budget) continue;

            // Confidence collapses sharply with distance so 1 edit on a 5-letter
            // word (0.80) is borderline and 2 edits (0.60) drops below the
            // report floor.
            var confidence = 1.0 - (double)d / single.Length;
            if (bestFuzzy is null || confidence > bestFuzzy.Confidence)
            {
                bestFuzzy = new ProhibitedItemMatch
                {
                    ItemId = item.Id,
                    ItemName = item.Name,
                    Category = item.Category,
                    MatchedTerm = rawTerm,
                    Evidence = tok,
                    MatchType = ProhibitedMatchType.Fuzzy,
                    Confidence = confidence
                };
            }
        }

        if (bestFuzzy is not null) Record(sink, bestFuzzy);
    }

    private static int FuzzyBudget(int termLength) => termLength switch
    {
        < MinFuzzyTokenLength => 0,
        <= 6 => 1,
        <= 10 => 2,
        _ => 2
    };

    private static void Record(
        Dictionary<(string itemId, ProhibitedMatchType type), ProhibitedItemMatch> sink,
        ProhibitedItemMatch match)
    {
        var key = (match.ItemId, match.MatchType);
        if (!sink.TryGetValue(key, out var existing) || match.Confidence > existing.Confidence)
        {
            sink[key] = match;
        }
    }
}
