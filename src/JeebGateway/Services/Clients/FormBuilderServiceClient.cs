using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IFormBuilderServiceClient"/>.
/// Targets form-builder-service's template/component/localization routes. The
/// named "form-builder" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress + the org-standard bearer / X-Service-Auth / resilience chain, so
/// this class never has to think about retry/timeout/circuit-breaker.
///
/// form-builder-service is a FastAPI app emitting camelCase-or-snake_case JSON
/// depending on the template payload; the stable identity fields the gateway
/// binds (<c>name</c>, <c>title</c>, <c>version</c>) bind under
/// <see cref="JsonSerializerDefaults.Web"/> (case-insensitive). The variable,
/// configuration-driven template/schema bodies are carried through verbatim as
/// <see cref="JsonElement"/> so the gateway never couples to their shape.
///
/// Localization is per call via the <c>Accept-Language</c> header (the upstream's
/// <c>accept_language</c> dependency, default "en").
/// </summary>
public sealed class FormBuilderServiceClient : IFormBuilderServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public FormBuilderServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<FormTemplateSummary>> ListTemplatesAsync(
        string acceptLanguage,
        CancellationToken ct)
    {
        // GET /templates
        using var request = BuildGet("templates", acceptLanguage);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<List<FormTemplateSummary>>(JsonOptions, ct);
        return payload ?? new List<FormTemplateSummary>();
    }

    public async Task<FormTemplateDocument> GetTemplateAsync(
        string templateName,
        string acceptLanguage,
        CancellationToken ct)
    {
        // GET /templates/{template_name}
        var path = $"templates/{Uri.EscapeDataString(templateName)}";
        using var request = BuildGet(path, acceptLanguage);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return new FormTemplateDocument
        {
            Name = templateName,
            Document = document.Clone(),
        };
    }

    public async Task<FormSchemaDocument> SchemaAsync(
        string templateName,
        string acceptLanguage,
        CancellationToken ct)
    {
        // GET /templates/{template_name}/schema — the KYC schema seam
        // (template_name = jeeb_jeeber_v1 -> versioned Jeeb KYC form schema).
        var path = $"templates/{Uri.EscapeDataString(templateName)}/schema";
        using var request = BuildGet(path, acceptLanguage);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var schema = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        return new FormSchemaDocument
        {
            TemplateName = templateName,
            Schema = schema.Clone(),
        };
    }

    public async Task<IReadOnlyList<string>> ListLanguagesAsync(CancellationToken ct)
    {
        // GET /languages -> { "supported_languages": ["en", "ar", ...] }
        using var response = await _http.GetAsync("languages", ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SupportedLanguagesWire>(JsonOptions, ct);
        return payload?.SupportedLanguages ?? new List<string>();
    }

    /// <summary>
    /// Builds a GET with the upstream's <c>Accept-Language</c> localization header.
    /// Falls back to "en" (the upstream default) when unset/blank.
    /// </summary>
    private static HttpRequestMessage BuildGet(string relativePath, string? acceptLanguage)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        var lang = string.IsNullOrWhiteSpace(acceptLanguage) ? "en" : acceptLanguage.Trim();
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(lang));
        return request;
    }

    // --- wire DTOs ---

    private sealed class SupportedLanguagesWire
    {
        // form-builder-service (FastAPI) emits snake_case for this envelope:
        // GET /languages -> { "supported_languages": [...] }. JsonSerializerDefaults.Web
        // would look for "supportedLanguages", so bind the snake_case key explicitly.
        [JsonPropertyName("supported_languages")]
        public List<string>? SupportedLanguages { get; init; }
    }
}
