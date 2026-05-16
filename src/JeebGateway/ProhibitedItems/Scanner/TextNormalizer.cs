using System.Globalization;
using System.Text;

namespace JeebGateway.ProhibitedItems.Scanner;

/// <summary>
/// Normalization is the load-bearing step for false-positive control: we want
/// "Kn1fe.", "knife,", and "KNIFE" to all reduce to "knife" before we run any
/// fuzzy distance. Stripping diacritics also makes the matcher correct on
/// transliterated Arabic / French entries without per-locale logic.
/// </summary>
internal static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);

        foreach (var c in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '/' || c == '.' || c == ',')
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            // everything else (control, symbol, currency, etc.) is dropped
        }

        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    public static IReadOnlyList<string> Tokenize(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
