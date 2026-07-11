using System.IO;
using System.Linq;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249 — shared source-scan utility for the upstream-exception sanitization guard
/// tests. Locates a controller <c>.cs</c> from the test bin dir and counts token occurrences
/// in its LIVE (comment-stripped) source, so a future single-site revert to a
/// <c>detail: ex.Message</c> leak trips the guard. Factors out the inline grep-guard idiom
/// from <see cref="ChatControllerErrorShapeTests"/> for the five Jeeb* BFF guard tests.
/// </summary>
internal static class ControllerSourceScan
{
    /// <summary>Walk up from the test bin dir to find src/JeebGateway/Controllers/&lt;fileName&gt;.</summary>
    public static string? Locate(string controllerFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway", "Controllers", controllerFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// The file's source with whole-line comments (<c>///</c> XML docs and <c>//</c> remarks)
    /// removed, so a helper's own doc that cites the retired pattern does not count as a live
    /// occurrence. Line-level filtering is the safe scan: trailing comments never carry the
    /// scanned tokens, and stripping after an inline "//" would corrupt "https://" literals.
    /// </summary>
    public static string LiveCode(string path)
        => string.Join(
            "\n",
            File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    public static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
