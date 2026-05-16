namespace JeebGateway.ProhibitedItems.Scanner;

/// <summary>
/// Optimal-string-alignment (restricted Damerau-Levenshtein) distance.
/// Adjacent transpositions cost 1, on top of the usual insert/delete/replace.
/// We cap the result so long mismatches short-circuit instead of running the
/// full matrix, which keeps the per-token cost bounded as the catalog grows.
/// </summary>
public static class DamerauLevenshtein
{
    public static int Distance(string a, string b, int max)
    {
        if (max < 0) max = 0;
        if (a == b) return 0;
        if (a.Length == 0) return b.Length <= max ? b.Length : max + 1;
        if (b.Length == 0) return a.Length <= max ? a.Length : max + 1;

        // Cheap length-difference prune: distance is at least |len(a)-len(b)|.
        var lenDiff = Math.Abs(a.Length - b.Length);
        if (lenDiff > max) return max + 1;

        var prevPrev = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var del = prev[j] + 1;
                var ins = curr[j - 1] + 1;
                var sub = prev[j - 1] + cost;
                var value = Math.Min(Math.Min(del, ins), sub);

                if (i > 1 && j > 1
                    && a[i - 1] == b[j - 2]
                    && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, prevPrev[j - 2] + 1);
                }

                curr[j] = value;
                if (value < rowMin) rowMin = value;
            }

            // If every cell in this row already exceeds max, no descent can recover.
            if (rowMin > max) return max + 1;

            (prevPrev, prev, curr) = (prev, curr, prevPrev);
        }

        return prev[b.Length];
    }
}
