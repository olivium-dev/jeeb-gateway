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
/// D17. Real jeeber onboarding died at "Submit for review" because
/// <c>GET /v1/kyc/contract-template</c> answered 404 "No contract-signing
/// template named 'jeeb_tos_v1' is registered.". The live catalog was genuinely
/// empty, but the resolver had two ways to report a template as missing that had
/// nothing to do with the data:
///
///   1. it read the UNFILTERED collection route, so the upstream's default page
///      (limit=50) decided whether a registered template was visible at all;
///   2. its fail-open second pass accepted an explicitly DISABLED row, so a
///      retired ToS version could still be served and signed.
///
/// These tests pin both, plus the honest 404 for a truly empty catalog.
/// </summary>
public sealed class KycTosTemplateResolutionD17Tests
    : IClassFixture<KycTosTemplateResolutionD17Tests.ResolutionFactory>
{
    private readonly ResolutionFactory _factory;

    public KycTosTemplateResolutionD17Tests(ResolutionFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Resolver_Asks_The_Upstream_For_The_Template_By_Name()
    {
        // The upstream documents ?name= as the gateway's slug -> ULID resolution
        // path. Reading the unfiltered collection instead is what makes the
        // answer depend on page size.
        _factory.ContractSigning.Reset();
        _factory.ContractSigning.Register("tmpl-tos-1", "jeeb_tos_v1", "ACTIVE");

        var resp = await ClientFor("d17-user-name").GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ContractSigning.RequestedNames.Should().Contain("jeeb_tos_v1");
    }

    [Fact]
    public async Task Registered_Template_Beyond_The_First_Page_Is_Still_Found()
    {
        // 60 other templates registered first: with an unfiltered read the
        // upstream's default limit=50 page never contains jeeb_tos_v1 and the
        // gateway 404s on data that IS registered.
        _factory.ContractSigning.Reset();
        for (var i = 0; i < 60; i++)
        {
            _factory.ContractSigning.Register($"tmpl-other-{i}", $"other_tc_v{i}", "ACTIVE");
        }
        _factory.ContractSigning.Register("tmpl-tos-late", "jeeb_tos_v1", "ACTIVE");

        var resp = await ClientFor("d17-user-page").GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(resp)).GetProperty("template_id").GetString()
            .Should().Be("tmpl-tos-late");
    }

    [Fact]
    public async Task Only_A_DISABLED_Row_Is_Not_Silently_Served()
    {
        // A retired ToS must not be handed out and counter-signed. 404 here is
        // the honest answer and matches "no ACTIVE template is registered".
        _factory.ContractSigning.Reset();
        _factory.ContractSigning.Register("tmpl-tos-retired", "jeeb_tos_v1", "DISABLED");

        var resp = await ClientFor("d17-user-disabled").GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_Catalog_Still_404s_With_The_Naming_Detail()
    {
        // Negative control with teeth: the SAME request that returns 200 above
        // returns 404 here, so a passing case is a real resolution and not an
        // endpoint that always succeeds.
        _factory.ContractSigning.Reset();

        var resp = await ClientFor("d17-user-empty").GetAsync("/v1/kyc/contract-template");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("detail").GetString().Should().Contain("jeeb_tos_v1");
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

    public sealed class ResolutionFactory : WebApplicationFactory<Program>
    {
        public PagingCatalogClient ContractSigning { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FeatureFlags:UseUpstream:ContractSigning", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IContractSigningServiceClient>();
                services.AddSingleton<IContractSigningServiceClient>(ContractSigning);
            });
        }
    }

    /// <summary>
    /// Contract-signing fake that reproduces the upstream's paging contract:
    /// the unfiltered collection route returns at most <c>DefaultLimit</c> rows,
    /// while <c>?name=</c> filters server-side across the whole catalog.
    /// </summary>
    public sealed class PagingCatalogClient : IContractSigningServiceClient
    {
        private const int DefaultLimit = 50;
        private readonly List<(string Id, string Name, string? Status)> _rows = new();

        public List<string?> RequestedNames { get; } = new();

        public void Reset()
        {
            _rows.Clear();
            RequestedNames.Clear();
        }

        public void Register(string id, string name, string? status) =>
            _rows.Add((id, name, status));

        public Task<JsonElement> ListTemplatesAsync(CancellationToken ct) =>
            ListTemplatesAsync(null, ct);

        public Task<JsonElement> ListTemplatesAsync(string? name, CancellationToken ct)
        {
            RequestedNames.Add(name);
            var matched = name is null
                ? _rows.Take(DefaultLimit)
                : _rows.Where(r => r.Name == name).Take(DefaultLimit);
            var items = matched.Select(r => r.Status is null
                ? $"{{\"template_id\":\"{r.Id}\",\"name\":\"{r.Name}\"}}"
                : $"{{\"template_id\":\"{r.Id}\",\"name\":\"{r.Name}\",\"status\":\"{r.Status}\"}}");
            using var doc = JsonDocument.Parse($"{{\"items\":[{string.Join(",", items)}]}}");
            return Task.FromResult(doc.RootElement.Clone());
        }

        public Task<ContractTemplate> RegisterTemplateAsync(RegisterTemplateRequest request, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = "tmpl-new", Name = request.Name, Status = "ACTIVE" });

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
