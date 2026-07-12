using System.Net;
using System.Net.Http.Json;
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
/// JEBV4-247 (l10n). GET /v1/kyc/contract-template must serve the ToS
/// <c>document_url</c> whose locale matches the caller's Accept-Language.
///
/// The live contract-signing MediaRef schema is {type, url, label, checksum} —
/// there is NO <c>locale</c> field, so ResolveDocumentUrl matches on the real
/// <c>label</c> tag (seeded "en"/"ar"). Before the fix it read a nonexistent
/// <c>locale</c> field and always fell through to media[0] (English-first),
/// serving the English ToS to Arabic users — a compliance defect that would
/// surface the moment real multi-locale content is seeded (JEBV4-233 / G10).
///
/// The contract-signing upstream is a FAKE living only in the test assembly;
/// the seam wiring (locale header -> resolved document_url) is the production path.
/// </summary>
public sealed class KycTosDocumentLocaleTests
    : IClassFixture<KycTosDocumentLocaleTests.LocaleFactory>
{
    private const string EnUrl = "cdn://obj/tos_en_v1.pdf";
    private const string ArUrl = "cdn://obj/tos_ar_v1.pdf";

    private readonly LocaleFactory _factory;

    public KycTosDocumentLocaleTests(LocaleFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ContractTemplate_ArabicCaller_Gets_Arabic_DocumentUrl()
    {
        var client = ClientFor("loc-ar-user", acceptLanguage: "ar-LB,ar;q=0.9,en;q=0.8");
        var resp = await client.GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("locale").GetString().Should().Be("ar");
        json.GetProperty("document_url").GetString().Should().Be(ArUrl);
    }

    [Fact]
    public async Task ContractTemplate_EnglishCaller_Gets_English_DocumentUrl()
    {
        var client = ClientFor("loc-en-user", acceptLanguage: "en-US,en;q=0.9");
        var resp = await client.GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("locale").GetString().Should().Be("en");
        json.GetProperty("document_url").GetString().Should().Be(EnUrl);
    }

    [Fact]
    public async Task ContractTemplate_NoAcceptLanguage_Defaults_To_English_DocumentUrl()
    {
        // PreferredLocale() defaults to "en" when no Accept-Language is present.
        var client = ClientFor("loc-default-user", acceptLanguage: null);
        var resp = await client.GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("locale").GetString().Should().Be("en");
        json.GetProperty("document_url").GetString().Should().Be(EnUrl);
    }

    // ----- helpers -----

    private HttpClient ClientFor(string userId, string? acceptLanguage)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    public sealed class LocaleFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Kyc", "true");
            builder.UseSetting("FeatureFlags:UseUpstream:ContractSigning", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IContractSigningServiceClient>();
                services.AddSingleton<IContractSigningServiceClient, MultiLocaleTosCatalogClient>();
            });
        }
    }

    /// <summary>
    /// Fake contract-signing catalog seeding jeeb_tos_v1 with a multi-locale
    /// <c>media</c> array shaped like the real MediaRef ({type, url, label,
    /// checksum}) — the locale tag rides on <c>label</c>, NOT a locale field.
    /// media[0] is English so a locale-blind resolver would always return English;
    /// the ar assertion only passes if the fix matches on label.
    /// </summary>
    private sealed class MultiLocaleTosCatalogClient : IContractSigningServiceClient
    {
        public Task<JsonElement> ListTemplatesAsync(CancellationToken ct)
        {
            const string catalog = """
            {
              "items": [
                {
                  "template_id": "tmpl-tos-1",
                  "name": "jeeb_tos_v1",
                  "status": "ACTIVE",
                  "media": [
                    { "type": "application/pdf", "url": "cdn://obj/tos_en_v1.pdf", "label": "en", "checksum": "sha256:aa" },
                    { "type": "application/pdf", "url": "cdn://obj/tos_ar_v1.pdf", "label": "ar", "checksum": "sha256:bb" }
                  ]
                }
              ]
            }
            """;
            using var doc = JsonDocument.Parse(catalog);
            return Task.FromResult(doc.RootElement.Clone());
        }

        public Task<Contract> CreateContractAsync(CreateContractRequest request, CancellationToken ct)
            => Task.FromResult(new Contract { ContractId = "ctr_local", TemplateId = request.TemplateId, Status = "ACTIVE", Stage = "DRAFT" });

        public Task<Signature> SignAsync(string contractId, SignRequest request, CancellationToken ct)
            => Task.FromResult(new Signature { ContractId = contractId, RoleKey = request.RoleKey, PartyRef = request.PartyRef, SignedAt = DateTimeOffset.UtcNow });

        public Task<ContractTemplate> RegisterTemplateAsync(RegisterTemplateRequest request, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = "tmpl-tos-1", Name = request.Name, Status = "ACTIVE" });

        public Task<ContractTemplate> GetTemplateAsync(string templateId, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = templateId, Status = "ACTIVE" });

        public Task<Contract> GetContractAsync(string contractId, CancellationToken ct)
            => Task.FromResult(new Contract { ContractId = contractId, Status = "ACTIVE", Stage = "DRAFT" });
    }
}
