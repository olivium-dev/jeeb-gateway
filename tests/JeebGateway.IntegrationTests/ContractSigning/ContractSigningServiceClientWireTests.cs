using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.ContractSigning;

/// <summary>
/// CI-SAFE contract test for the gateway↔contract-signing-service seam (thin-BFF
/// wire, behind <c>FeatureFlags:UseUpstream:ContractSigning</c>).
/// contract-signing-service is NOT yet deployed to the Jeeb swarm, so there is no
/// live box to hit; these tests drive the PRODUCTION
/// <see cref="ContractSigningServiceClient"/> over a stub
/// <see cref="HttpMessageHandler"/> that returns the literal FastAPI JSON shapes
/// (snake_case, api_prefix <c>/v1</c>) from
/// <c>contract-signing-service/app/routers/*.py</c>. They break instead of prod
/// if the path / casing / request-body contract drifts.
/// </summary>
public sealed class ContractSigningServiceClientWireTests
{
    private const string BaseUrl = "http://contract-signing.test";

    [Fact]
    public async Task RegisterTemplateAsync_Posts_jeeb_tos_v1_With_SnakeCase_Body()
    {
        // POST /v1/templates — register the versioned Jeeb ToS template
        // jeeb_tos_v1 (JEB-40 / JEB-41). Body must be snake_case so the upstream
        // Pydantic CreateTemplateRequest binds (stage_model, party_requirements).
        const string responseBody = """
        { "template_id": "tpl_123", "name": "jeeb_tos_v1", "status": "ACTIVE",
          "stage_model": ["DRAFT", "ACCEPTED"], "media": [], "references": [],
          "created_at": "2026-06-02T00:00:00Z", "party_requirements": [] }
        """;

        string? capturedBody = null;
        var handler = new CapturingHandler(async (req, ct) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.Should().Be("/v1/templates");
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Respond(responseBody, HttpStatusCode.Created);
        });

        var client = new ContractSigningServiceClient(NewHttp(handler));

        var request = new RegisterTemplateRequest
        {
            Name = "jeeb_tos_v1",
            Description = "Jeeb Terms of Service v1",
            StageModel = new[] { "DRAFT", "ACCEPTED" },
            PartyRequirements = new[]
            {
                new PartyRequirement { RoleKey = "acceptor", RequiredCount = 1, SignatureOrder = 1 },
            },
        };

        var template = await client.RegisterTemplateAsync(request, CancellationToken.None);

        // Response binds.
        template.TemplateId.Should().Be("tpl_123");
        template.Name.Should().Be("jeeb_tos_v1");
        template.Status.Should().Be("ACTIVE");
        template.StageModel.Should().BeEquivalentTo(new[] { "DRAFT", "ACCEPTED" });

        // Outbound body is snake_case (upstream contract).
        capturedBody.Should().NotBeNull();
        using var sent = JsonDocument.Parse(capturedBody!);
        sent.RootElement.GetProperty("name").GetString().Should().Be("jeeb_tos_v1");
        sent.RootElement.GetProperty("stage_model")[0].GetString().Should().Be("DRAFT");
        sent.RootElement.GetProperty("party_requirements")[0]
            .GetProperty("role_key").GetString().Should().Be("acceptor");
        sent.RootElement.GetProperty("party_requirements")[0]
            .GetProperty("required_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SignAsync_Posts_Signature_To_Contract_Signatures_Path()
    {
        // POST /v1/contracts/{id}/signatures — the Jeeb ToS acceptance seam.
        const string responseBody = """
        { "contract_id": "ctr_9", "role_key": "acceptor", "party_ref": "user_42",
          "signed_at": "2026-06-02T01:02:03Z", "signature_proof_ref": "proof_abc" }
        """;

        string? capturedBody = null;
        var handler = new CapturingHandler(async (req, ct) =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.Should().Be("/v1/contracts/ctr_9/signatures");
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return Respond(responseBody, HttpStatusCode.Created);
        });

        var client = new ContractSigningServiceClient(NewHttp(handler));

        var signature = await client.SignAsync(
            "ctr_9",
            new SignRequest { RoleKey = "acceptor", PartyRef = "user_42", SignatureProofRef = "proof_abc" },
            CancellationToken.None);

        signature.ContractId.Should().Be("ctr_9");
        signature.RoleKey.Should().Be("acceptor");
        signature.PartyRef.Should().Be("user_42");
        signature.SignatureProofRef.Should().Be("proof_abc");
        signature.SignedAt.Should().NotBeNull();

        capturedBody.Should().NotBeNull();
        using var sent = JsonDocument.Parse(capturedBody!);
        sent.RootElement.GetProperty("role_key").GetString().Should().Be("acceptor");
        sent.RootElement.GetProperty("party_ref").GetString().Should().Be("user_42");
        sent.RootElement.GetProperty("signature_proof_ref").GetString().Should().Be("proof_abc");
    }

    [Fact]
    public async Task CreateContractAsync_Posts_To_Contracts_And_Binds_Response()
    {
        const string responseBody = """
        { "contract_id": "ctr_9", "template_id": "tpl_123", "status": "OPEN",
          "stage": "DRAFT", "parties": [ { "role_key": "acceptor", "party_ref": "user_42" } ] }
        """;
        var handler = new CapturingHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/v1/contracts");
            return Task.FromResult(Respond(responseBody, HttpStatusCode.Created));
        });

        var client = new ContractSigningServiceClient(NewHttp(handler));

        var contract = await client.CreateContractAsync(
            new CreateContractRequest
            {
                TemplateId = "tpl_123",
                Parties = new[] { new ContractParty { RoleKey = "acceptor", PartyRef = "user_42" } },
            },
            CancellationToken.None);

        contract.ContractId.Should().Be("ctr_9");
        contract.TemplateId.Should().Be("tpl_123");
        contract.Status.Should().Be("OPEN");
        contract.Stage.Should().Be("DRAFT");
        contract.Document.GetProperty("parties")[0]
            .GetProperty("party_ref").GetString().Should().Be("user_42");
    }

    [Fact]
    public async Task GetTemplateAsync_Hits_Template_By_Id_Path()
    {
        const string responseBody = """{ "template_id": "tpl_123", "name": "jeeb_tos_v1", "status": "ACTIVE" }""";
        var handler = new CapturingHandler((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.AbsolutePath.Should().Be("/v1/templates/tpl_123");
            return Task.FromResult(Respond(responseBody, HttpStatusCode.OK));
        });

        var client = new ContractSigningServiceClient(NewHttp(handler));

        var template = await client.GetTemplateAsync("tpl_123", CancellationToken.None);

        template.TemplateId.Should().Be("tpl_123");
        template.Name.Should().Be("jeeb_tos_v1");
    }

    [Fact]
    public async Task ListTemplatesAsync_Hits_Bare_Collection_Path_Not_List_Suffix()
    {
        // GET /v1/templates — the PostgreSQL-backed catalog read. CRITICAL: it must
        // hit the BARE collection path. The upstream resolves /v1/templates/list to
        // the by-id route (id="list" -> 404), which is the exact 500/404 the gateway
        // route is fixing — so this asserts the client never appends "/list".
        const string responseBody = """
        { "items": [ { "template_id": "tpl_123", "name": "jeeb_tos_v1", "status": "ACTIVE" } ],
          "total": 1, "limit": 50, "offset": 0 }
        """;
        var handler = new CapturingHandler((req, _) =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.AbsolutePath.Should().Be("/v1/templates");
            req.RequestUri!.AbsolutePath.Should().NotContain("/list");
            return Task.FromResult(Respond(responseBody, HttpStatusCode.OK));
        });

        var client = new ContractSigningServiceClient(NewHttp(handler));

        var doc = await client.ListTemplatesAsync(CancellationToken.None);

        doc.GetProperty("total").GetInt32().Should().Be(1);
        doc.GetProperty("items")[0].GetProperty("template_id").GetString().Should().Be("tpl_123");
        doc.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("jeeb_tos_v1");
    }

    [Fact]
    public async Task ListTemplatesAsync_NonSuccess_Throws_HttpRequestException()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = new ContractSigningServiceClient(NewHttp(handler));

        var act = () => client.ListTemplatesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task NonSuccess_Throws_HttpRequestException()
    {
        var handler = new CapturingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var client = new ContractSigningServiceClient(NewHttp(handler));

        var act = () => client.SignAsync(
            "ctr_9",
            new SignRequest { RoleKey = "acceptor", PartyRef = "user_42" },
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static HttpClient NewHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/") };

    private static HttpResponseMessage Respond(string json, HttpStatusCode status) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }
}
