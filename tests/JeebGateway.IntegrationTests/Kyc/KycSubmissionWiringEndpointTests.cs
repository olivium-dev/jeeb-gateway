using System.Net;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Kyc;

/// <summary>
/// JEB-1473 — the gateway now OWNS the Jeeb KYC submission / approval-unlock
/// wiring (moved out of the shared form-builder-service, whose
/// <c>GET /templates/{name}/submission</c> endpoint + hardcoded
/// <c>auth-service:/api/jeeb/users/me/role</c> default were removed).
///
/// <para>
/// <see cref="JeebGateway.Controllers.KycBffController.GetSubmissionWiring"/>
/// confirms the canonical template resolves upstream via the typed
/// <see cref="IFormBuilderServiceClient"/> (GR4) and composes the downstream
/// wiring from the gateway-owned Jeeb-domain mapping (GR2). These tests drive
/// the production controller over a fake form-builder client.
/// </para>
/// </summary>
public sealed class KycSubmissionWiringEndpointTests
{
    [Fact]
    public async Task SubmissionWiring_Returns_GatewayOwned_ApprovalUnlocks_And_Tos()
    {
        using var factory = new WiringFactory(formBuilderOn: true, templateExists: true);
        var client = ClientFor(factory, "wiring-user-1");

        var resp = await client.GetAsync("/v1/kyc/jeeb/submission-wiring");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(resp);

        body.GetProperty("canonical_template_id").GetString().Should().Be("jeeb_jeeber_v1");
        body.GetProperty("template_version").GetString().Should().Be("v1");

        var downstream = body.GetProperty("submission").GetProperty("downstream");
        downstream.GetProperty("approvalUnlocks").GetString()
            .Should().Be("user-management:/api/v1/users/me/role");
        downstream.GetProperty("acceptedTosTemplateId").GetString()
            .Should().Be("jeeb_tos_v1");

        // The resolution genuinely consulted the typed form-builder client.
        factory.FormBuilder.SchemaCallCount.Should().Be(1);
        factory.FormBuilder.LastSchemaTemplate.Should().Be("jeeb_jeeber_v1");
    }

    [Fact]
    public async Task SubmissionWiring_Returns_404_When_Template_Not_Registered_Upstream()
    {
        using var factory = new WiringFactory(formBuilderOn: true, templateExists: false);
        var client = ClientFor(factory, "wiring-user-2");

        var resp = await client.GetAsync("/v1/kyc/jeeb/submission-wiring");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SubmissionWiring_Returns_503_When_FormBuilder_Disabled()
    {
        using var factory = new WiringFactory(formBuilderOn: false, templateExists: true);
        var client = ClientFor(factory, "wiring-user-3");

        var resp = await client.GetAsync("/v1/kyc/jeeb/submission-wiring");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private static HttpClient ClientFor(WiringFactory factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    private sealed class WiringFactory : WebApplicationFactory<Program>
    {
        private readonly bool _formBuilderOn;
        public FakeFormBuilder FormBuilder { get; }

        public WiringFactory(bool formBuilderOn, bool templateExists)
        {
            _formBuilderOn = formBuilderOn;
            FormBuilder = new FakeFormBuilder(templateExists);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:UseUpstream:FormBuilder", _formBuilderOn ? "true" : "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFormBuilderServiceClient>();
                services.AddSingleton<IFormBuilderServiceClient>(FormBuilder);
            });
        }
    }

    /// <summary>Minimal fake form-builder client for the wiring projection test.</summary>
    private sealed class FakeFormBuilder : IFormBuilderServiceClient
    {
        private readonly bool _templateExists;
        public int SchemaCallCount { get; private set; }
        public string? LastSchemaTemplate { get; private set; }

        public FakeFormBuilder(bool templateExists) => _templateExists = templateExists;

        public Task<FormSchemaDocument> SchemaAsync(string templateName, string acceptLanguage, CancellationToken ct)
        {
            SchemaCallCount++;
            LastSchemaTemplate = templateName;
            if (!_templateExists)
            {
                throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            }

            using var doc = JsonDocument.Parse("""{ "template_name": "jeeb_jeeber_v1", "components": [], "required_fields": [] }""");
            return Task.FromResult(new FormSchemaDocument
            {
                TemplateName = templateName,
                Schema = doc.RootElement.Clone(),
            });
        }

        public Task<IReadOnlyList<FormTemplateSummary>> ListTemplatesAsync(string acceptLanguage, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FormTemplateSummary>>(Array.Empty<FormTemplateSummary>());

        public Task<FormTemplateDocument> GetTemplateAsync(string templateName, string acceptLanguage, CancellationToken ct)
            => Task.FromResult(new FormTemplateDocument { Name = templateName });

        public Task<IReadOnlyList<string>> ListLanguagesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "en", "ar" });

        public Task<JsonElement> SubmitFormAsync(string templateName, JsonElement body, CancellationToken ct)
            => Task.FromResult(default(JsonElement));

        public Task<JsonElement> GetFormSubmissionAsync(string templateName, string formId, CancellationToken ct)
            => Task.FromResult(default(JsonElement));
    }
}
