using System.Net.Http.Headers;

namespace JeebGateway.FormSubmissions;

public static class FormSubmissionLanguage
{
    public static string Resolve(string header)
    {
        StringWithQualityHeaderValue? preferred = null;
        foreach (var token in header.Split(','))
        {
            if (!StringWithQualityHeaderValue.TryParse(token.Trim(), out var value) ||
                value.Value == "*" || value.Quality == 0) continue;
            if (preferred is null || (value.Quality ?? 1) > (preferred.Quality ?? 1)) preferred = value;
        }
        return preferred?.Value ?? "en";
    }
}
