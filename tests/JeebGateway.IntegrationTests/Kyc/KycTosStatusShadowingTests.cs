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
/// JEBV4-257. The two BFF helpers that resolve the live <c>jeeb_tos_v1</c>
/// contract-signing template must NOT take the first name-match blindly:
/// contract-signing templates are create-once/disable-only (a new version is a
/// new row + the old one disabled), so a DISABLED same-named row earlier in
/// enumeration order used to silently shadow the ACTIVE one. The resolver now
/// prefers the ACTIVE name-match (pass 1) and only falls back to the first
/// name-match when status is unpopulated (pass 2, fail-open).
///
/// Covered here via <c>GET /v1/kyc/contract-template</c>, whose projected
/// <c>template_id</c> is read straight off the resolved catalog row — so the
/// disabled-vs-active choice is directly observable.
/// </summary>
public sealed class KycTosStatusShadowingTests
    : IClassFixture<KycTosStatusShadowingTests.ShadowingFactory>
{
    private readonly ShadowingFactory _factory;

    public KycTosStatusShadowingTests(ShadowingFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetContractTemplate_Picks_ACTIVE_Row_When_Disabled_SameName_Row_Enumerated_First()
    {
        // Disabled jeeb_tos_v1 (tmpl-tos-OLD) is FIRST; ACTIVE jeeb_tos_v1
        // (tmpl-tos-ACTIVE) is second. The old first-match logic returned
        // tmpl-tos-OLD; the ACTIVE-first pass must return tmpl-tos-ACTIVE.
        _factory.ContractSigning.CatalogJson =
            "{\"items\":[" +
            "{\"template_id\":\"tmpl-tos-OLD\",\"name\":\"jeeb_tos_v1\",\"status\":\"DISABLED\"}," +
            "{\"template_id\":\"tmpl-tos-ACTIVE\",\"name\":\"jeeb_tos_v1\",\"status\":\"ACTIVE\"}]}";

        var client = ClientFor("shadow-user-1");
        var resp = await client.GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("template_id").GetString().Should().Be("tmpl-tos-ACTIVE");
    }

    [Fact]
    public async Task GetContractTemplate_Falls_Back_To_First_NameMatch_When_Status_Unpopulated()
    {
        // No row carries an ACTIVE status (upstream left it unpopulated). Pass 2
        // must fail-open and still resolve the (only) name-match rather than 404.
        _factory.ContractSigning.CatalogJson =
            "{\"items\":[" +
            "{\"template_id\":\"tmpl-tos-NOSTATUS\",\"name\":\"jeeb_tos_v1\"}]}";

        var client = ClientFor("shadow-user-2");
        var resp = await client.GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("template_id").GetString().Should().Be("tmpl-tos-NOSTATUS");
    }

    // ----- helpers -----

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    public sealed class ShadowingFactory : WebApplicationFactory<Program>
    {
        public ConfigurableCatalogClient ContractSigning { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:UseUpstream:ContractSigning", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IContractSigningServiceClient>();
                services.AddSingleton<IContractSigningServiceClient>(ContractSigning);
            });
        }
    }

    /// <summary>Contract-signing fake whose catalog JSON is set per-test.</summary>
    public sealed class ConfigurableCatalogClient : IContractSigningServiceClient
    {
        public string CatalogJson { get; set; } = "{\"items\":[]}";

        public Task<JsonElement> ListTemplatesAsync(CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(CatalogJson);
            return Task.FromResult(doc.RootElement.Clone());
        }

        public Task<ContractTemplate> RegisterTemplateAsync(RegisterTemplateRequest request, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = "tmpl-tos-1", Name = request.Name, Status = "ACTIVE" });

        public Task<ContractTemplate> GetTemplateAsync(string templateId, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = templateId, Status = "ACTIVE" });

        public Task<Contract> CreateContractAsync(CreateContractRequest request, CancellationToken ct)
            => Task.FromResult(new Contract { ContractId = "ctr_test", TemplateId = request.TemplateId, Status = "ACTIVE", Stage = "DRAFT" });

        public Task<Contract> GetContractAsync(string contractId, CancellationToken ct)
            => Task.FromResult(new Contract { ContractId = contractId, Status = "ACTIVE", Stage = "DRAFT" });

        public Task<Signature> SignAsync(string contractId, SignRequest request, CancellationToken ct)
            => Task.FromResult(new Signature
            {
                ContractId = contractId,
                RoleKey = request.RoleKey,
                PartyRef = request.PartyRef,
                SignedAt = DateTimeOffset.UtcNow,
                SignatureProofRef = request.SignatureProofRef,
            });
    }
}
