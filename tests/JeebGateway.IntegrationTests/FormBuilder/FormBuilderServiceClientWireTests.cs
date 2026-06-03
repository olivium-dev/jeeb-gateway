using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.FormBuilder;

/// <summary>
/// CI-SAFE contract test for the gateway↔form-builder-service seam (thin-BFF
/// wire, behind <c>FeatureFlags:UseUpstream:FormBuilder</c>). form-builder-service
/// is NOT yet deployed to the Jeeb swarm, so there is no live box to hit; these
/// tests drive the PRODUCTION <see cref="FormBuilderServiceClient"/> over a stub
/// <see cref="HttpMessageHandler"/> that returns the literal FastAPI JSON shapes
/// from <c>form-builder-service/app/main.py</c>. They break instead of prod if
/// the path/casing/Accept-Language contract drifts.
/// </summary>
public sealed class FormBuilderServiceClientWireTests
{
    private const string BaseUrl = "http://form-builder.test";

    [Fact]
    public async Task SchemaAsync_Hits_Template_Schema_Path_With_Accept_Language()
    {
        // GET /templates/jeeb_jeeber_v1/schema — the versioned Jeeb KYC schema
        // seam (JEB-40 / JEB-41).
        const string schemaBody = """
        { "title": "Jeeber KYC", "version": "v1", "fields": [ { "key": "id_front", "type": "image" } ] }
        """;

        var handler = new CapturingHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/templates/jeeb_jeeber_v1/schema");
            req.Headers.AcceptLanguage.ToString().Should().Contain("ar");
            return Respond(schemaBody);
        });

        var client = new FormBuilderServiceClient(NewHttp(handler));

        var result = await client.SchemaAsync("jeeb_jeeber_v1", "ar", CancellationToken.None);

        result.TemplateName.Should().Be("jeeb_jeeber_v1");
        result.Schema.GetProperty("version").GetString().Should().Be("v1");
        result.Schema.GetProperty("fields")[0].GetProperty("key").GetString().Should().Be("id_front");
    }

    [Fact]
    public async Task ListTemplatesAsync_Binds_Summary_Fields()
    {
        const string body = """
        [ { "name": "jeeb_jeeber_v1", "title": "Jeeber KYC", "version": "v1" } ]
        """;
        var handler = new CapturingHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/templates");
            return Respond(body);
        });

        var client = new FormBuilderServiceClient(NewHttp(handler));

        var templates = await client.ListTemplatesAsync("en", CancellationToken.None);

        templates.Should().ContainSingle();
        templates[0].Name.Should().Be("jeeb_jeeber_v1");
        templates[0].Title.Should().Be("Jeeber KYC");
    }

    [Fact]
    public async Task ListLanguagesAsync_Unwraps_Supported_Languages_Envelope()
    {
        const string body = """{ "supported_languages": ["en", "ar"] }""";
        var handler = new CapturingHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/languages");
            return Respond(body);
        });

        var client = new FormBuilderServiceClient(NewHttp(handler));

        var languages = await client.ListLanguagesAsync(CancellationToken.None);

        languages.Should().BeEquivalentTo(new[] { "en", "ar" });
    }

    [Fact]
    public async Task SubmitFormAsync_Posts_Body_To_Forms_Path_And_Returns_SubmissionId()
    {
        // POST /forms/{template_name} — the PostgreSQL WRITE path (insert a row,
        // return the generated submission_id). The body is the template's
        // configuration-driven component value map, passed through verbatim.
        const string responseBody = """
        { "message": "jeeb_jeeber_v1 submitted successfully",
          "submission_id": "f3f2dc0a-8831-4e95-ab4c-e72b3ac47df2",
          "data": { "kyc-vehicle-plate-number": "JEEB-123" } }
        """;
        string? capturedPath = null;
        string? capturedBody = null;
        var handler = new CapturingHandler((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Respond(responseBody);
        });

        var client = new FormBuilderServiceClient(NewHttp(handler));

        using var bodyDoc = JsonDocument.Parse(
            """{ "kyc-vehicle-plate-number": { "value": "JEEB-123" } }""");
        var result = await client.SubmitFormAsync(
            "jeeb_jeeber_v1", bodyDoc.RootElement, CancellationToken.None);

        capturedPath.Should().Be("/forms/jeeb_jeeber_v1");
        capturedBody.Should().Contain("kyc-vehicle-plate-number");
        result.GetProperty("submission_id").GetString()
            .Should().Be("f3f2dc0a-8831-4e95-ab4c-e72b3ac47df2");
    }

    [Fact]
    public async Task GetFormSubmissionAsync_Reads_Back_By_Id_From_Forms_Path()
    {
        // GET /forms/{template_name}/{form_id} — the PostgreSQL READ-BACK path.
        const string responseBody = """
        { "message": "Retrieved jeeb_jeeber_v1 form",
          "submission_id": "f3f2dc0a-8831-4e95-ab4c-e72b3ac47df2",
          "data": { "kyc-vehicle-plate-number": { "value": "JEEB-123" } } }
        """;
        var handler = new CapturingHandler((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.AbsolutePath
                .Should().Be("/forms/jeeb_jeeber_v1/f3f2dc0a-8831-4e95-ab4c-e72b3ac47df2");
            return Respond(responseBody);
        });

        var client = new FormBuilderServiceClient(NewHttp(handler));

        var result = await client.GetFormSubmissionAsync(
            "jeeb_jeeber_v1", "f3f2dc0a-8831-4e95-ab4c-e72b3ac47df2", CancellationToken.None);

        result.GetProperty("submission_id").GetString()
            .Should().Be("f3f2dc0a-8831-4e95-ab4c-e72b3ac47df2");
        result.GetProperty("data").GetProperty("kyc-vehicle-plate-number")
            .GetProperty("value").GetString().Should().Be("JEEB-123");
    }

    [Fact]
    public async Task SubmitFormAsync_NonSuccess_Throws_HttpRequestException()
    {
        var handler = new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new FormBuilderServiceClient(NewHttp(handler));

        using var bodyDoc = JsonDocument.Parse("""{ "x": { "value": "y" } }""");
        var act = () => client.SubmitFormAsync("jeeb_jeeber_v1", bodyDoc.RootElement, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task NonSuccess_Throws_HttpRequestException()
    {
        var handler = new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new FormBuilderServiceClient(NewHttp(handler));

        var act = () => client.SchemaAsync("jeeb_jeeber_v1", "en", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static HttpClient NewHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/") };

    private static HttpResponseMessage Respond(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public CapturingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }
}
