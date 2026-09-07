using System.Text.Json;
using JeebGateway.Services.Clients;

namespace JeebGateway.IntegrationTests.FormSubmissions;

internal static class FormFixtures
{
    public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    public static JsonElement Body => Json("""{"state":"Beirut","country":"Lebanon","address":"Building","home_base":{"lat":33.89,"lng":35.5,"label":"Home"}}""");
    public static JsonElement Schema => Json("""
    {"components":[{"componentID":"portrait_object_ref","type":"string"},{"componentID":"state","type":"string"},
    {"componentID":"country","type":"string"},{"componentID":"street","type":"string"},{"componentID":"address","type":"string"},
    {"componentID":"home_base","type":"object"}],"required_fields":["state","country","address","home_base"]}
    """);
    public static JsonElement Document => Json("""
    [{"componentID":"portrait_object_ref","output":{"type":"string"},"validations":[]},
    {"componentID":"state","output":{"type":"string"},"validations":[{"rule":"required"},{"rule":"maxLength","value":256}]},
    {"componentID":"country","output":{"type":"string"},"validations":[{"rule":"required"},{"rule":"maxLength","value":256}]},
    {"componentID":"street","output":{"type":"string"},"validations":[{"rule":"maxLength","value":256}]},
    {"componentID":"address","output":{"type":"string"},"validations":[{"rule":"required"},{"rule":"maxLength","value":256}]},
    {"componentID":"home_base","output":{"type":"object"},"validations":[{"rule":"required"}]}]
    """);
}

internal sealed class FakeFormBuilder : IFormBuilderServiceClient
{
    public Exception? Failure { get; set; }
    public JsonElement Schema { get; set; } = FormFixtures.Schema;
    public JsonElement Document { get; set; } = FormFixtures.Document;
    public string? Language { get; private set; }
    public Task<FormSchemaDocument> SchemaAsync(string name, string language, CancellationToken ct)
    {
        Language = language;
        if (Failure is not null) throw Failure;
        return Task.FromResult(new FormSchemaDocument { TemplateName = name, Schema = Schema });
    }
    public Task<FormTemplateDocument> GetTemplateAsync(string name, string language, CancellationToken ct) =>
        Task.FromResult(new FormTemplateDocument { Name = name, Document = Document });
    public Task<IReadOnlyList<FormTemplateSummary>> ListTemplatesAsync(string language, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<string>> ListLanguagesAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> SubmitFormAsync(string name, JsonElement body, CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetFormSubmissionAsync(string name, string id, CancellationToken ct) => throw new NotSupportedException();
}
