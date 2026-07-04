using System.Collections.Concurrent;
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
/// S03 E1 (ToS-sign ceremony) + E2 (submit field validation) coverage.
///
/// E1: the ToS sign ceremony against the contract-signing primitive is
/// template → CONTRACT → signature (role_key "client"), NOT a one-shot
/// sign-by-template. With FeatureFlags:UseUpstream:ContractSigning ON these tests
/// assert the gateway calls CreateContract(template, parties[client], actor) THEN
/// Sign(contractId, role_key:"client") — the fix for the old 502 (signing a
/// template id as if it were a contract id → upstream 404 CONTRACT_NOT_FOUND).
///
/// E2: /v1/kyc/submit enforces id_number ^\d{12}$ (JEB-40 AC6) and the
/// tos_accepted_version cross-link (JEB-40 AC8) with field-scoped RFC7807, so
/// N2/N3 return 400 (not 201). Both upstreams are FAKES living only in the test
/// assembly — the seam wiring is the production path.
/// </summary>
public sealed class KycTosCeremonyAndValidationTests
    : IClassFixture<KycTosCeremonyAndValidationTests.CeremonyFactory>
{
    private readonly CeremonyFactory _factory;

    public KycTosCeremonyAndValidationTests(CeremonyFactory factory)
    {
        _factory = factory;
    }

    // ----- E1: ToS sign ceremony (CreateContract -> Sign role_key:client) -----

    [Fact]
    public async Task SignTos_With_ContractSigning_On_Does_CreateContract_Then_Sign_As_Client()
    {
        var fakeCs = _factory.ContractSigning;
        fakeCs.Reset();

        var client = ClientFor("ceremony-user-1");
        var resp = await PostJsonAsync(client, "/v1/kyc/contract-template/sign", new
        {
            template_id = "tmpl-tos-1",
            tos_version = "v1",
            signature_blob = "c2lnbmF0dXJl", // base64("signature")
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // CreateContract was called with the template, a single "client" party for
        // the user, and the user as actor.
        fakeCs.CreatedContracts.Should().ContainSingle();
        var created = fakeCs.CreatedContracts.Single();
        created.TemplateId.Should().Be("tmpl-tos-1");
        created.Parties.Should().ContainSingle(p => p.RoleKey == "client" && p.PartyRef == "ceremony-user-1");
        created.Actor.Ref.Should().Be("ceremony-user-1");

        // Sign was called against the CREATED contract id with role_key "client" —
        // never against the template id.
        fakeCs.Signatures.Should().ContainSingle();
        var sig = fakeCs.Signatures.Single();
        sig.RoleKey.Should().Be("client");
        sig.ContractId.Should().StartWith("ctr_");
        sig.ContractId.Should().NotBe("tmpl-tos-1");
    }

    // ----- E2: submit field validation (N2 id_number, N3 tos cross-link) -----

    [Fact]
    public async Task SubmitJson_N2_Bad_IdNumber_Returns_400_Field_ProblemDetails()
    {
        var client = ClientFor("e2-n2-user");
        var resp = await PostJsonAsync(client, "/v1/kyc/submit", Package(idNumber: "12AB"), Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
        json.GetProperty("type").GetString().Should().Contain("validation");
    }

    [Fact]
    public async Task SubmitJson_N3_Unknown_Tos_Version_Returns_400_Field_ProblemDetails()
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor("e2-n3-user");
        var resp = await PostJsonAsync(client, "/v1/kyc/submit", Package(tos: "unknown-tos-id"), Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("tos_accepted_version");
    }

    [Fact]
    public async Task SubmitJson_Happy_Valid_IdNumber_And_Known_Tos_Returns_201()
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor("e2-happy-user");
        var resp = await PostJsonAsync(client, "/v1/kyc/submit", Package(), Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ----- JEBV4-113 §3.1: id_number gate is scoped to id_type == national_id -----

    [Fact]
    public async Task SubmitJson_NationalId_Missing_IdNumber_Returns_400_Field_ProblemDetails()
    {
        var client = ClientFor("e3-national-missing-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: "national_id", idNumber: null), Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
    }

    [Theory]
    [InlineData("passport")]
    [InlineData("residency_permit")]
    public async Task SubmitJson_NonNationalId_With_No_IdNumber_Returns_201_Not_Blocked(string idType)
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor($"e3-nonnational-{idType}-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: idType, idNumber: null), Guid.NewGuid().ToString("N"));

        // Before the fix this 400'd unconditionally on the missing/12-digit
        // id_number check even though national-ID-shaped ids are irrelevant to
        // passport/residency_permit submissions (JEBV4-113 §3.1).
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SubmitJson_NonNationalId_With_BadShaped_IdNumber_Is_Not_Gated_Returns_201()
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor("e3-passport-freeform-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: "passport", idNumber: "P1234567"), Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ----- helpers -----

    private static object Package(string idType = "national_id", string? idNumber = "123456789012", string tos = "v1") => new
    {
        id_type = idType,
        id_number = idNumber,
        id_document_front_url = "cdn://obj/front",
        id_document_back_url = "cdn://obj/back",
        driver_license_number = "DL-99887766",
        driver_license_expiry = "2030-01-01",
        vehicle_registration_url = "cdn://obj/vehreg",
        vehicle_plate_number = "ABC-1234",
        vehicle_year_make_model = "2021 Toyota Corolla",
        selfie_with_liveness_url = "cdn://obj/selfie",
        tos_accepted_version = tos,
    };

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string path, object body, string idempotencyKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    public sealed class CeremonyFactory : WebApplicationFactory<Program>
    {
        public FakeContractSigningClient ContractSigning { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Kyc", "true");
            builder.UseSetting("FeatureFlags:UseUpstream:ContractSigning", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKycServiceClient>();
                services.AddSingleton<IKycServiceClient, CeremonyFakeKyc>();
                services.RemoveAll<IContractSigningServiceClient>();
                services.AddSingleton<IContractSigningServiceClient>(ContractSigning);
            });
        }
    }

    /// <summary>Records the ceremony calls so the test can assert template→contract→sign.</summary>
    public sealed class FakeContractSigningClient : IContractSigningServiceClient
    {
        public readonly List<CreateContractRequest> CreatedContracts = new();
        public readonly List<(string ContractId, string RoleKey)> Signatures = new();

        public void Reset()
        {
            CreatedContracts.Clear();
            Signatures.Clear();
        }

        // The catalog returns the seeded Jeeb client ToS template so the AC8 cross-link
        // resolves "v1" as the known version (and rejects "unknown-tos-id").
        public Task<JsonElement> ListTemplatesAsync(CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(
                "{\"items\":[{\"template_id\":\"tmpl-tos-1\",\"name\":\"jeeb-client-terms-and-conditions-v1\",\"status\":\"ACTIVE\"}]}");
            return Task.FromResult(doc.RootElement.Clone());
        }

        public Task<Contract> CreateContractAsync(CreateContractRequest request, CancellationToken ct)
        {
            CreatedContracts.Add(request);
            var id = "ctr_" + Guid.NewGuid().ToString("N")[..12];
            return Task.FromResult(new Contract { ContractId = id, TemplateId = request.TemplateId, Status = "ACTIVE", Stage = "DRAFT" });
        }

        public Task<Signature> SignAsync(string contractId, SignRequest request, CancellationToken ct)
        {
            Signatures.Add((contractId, request.RoleKey));
            return Task.FromResult(new Signature
            {
                ContractId = contractId,
                RoleKey = request.RoleKey,
                PartyRef = request.PartyRef,
                SignedAt = DateTimeOffset.UtcNow,
                SignatureProofRef = request.SignatureProofRef,
            });
        }

        public Task<ContractTemplate> RegisterTemplateAsync(RegisterTemplateRequest request, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = "tmpl-tos-1", Name = request.Name, Status = "ACTIVE" });

        public Task<ContractTemplate> GetTemplateAsync(string templateId, CancellationToken ct)
            => Task.FromResult(new ContractTemplate { TemplateId = templateId, Status = "ACTIVE" });

        public Task<Contract> GetContractAsync(string contractId, CancellationToken ct)
            => Task.FromResult(new Contract { ContractId = contractId, Status = "ACTIVE", Stage = "DRAFT" });
    }

    /// <summary>Minimal fake kyc-service for the ceremony/validation tests.</summary>
    private sealed class CeremonyFakeKyc : IKycServiceClient
    {
        private readonly ConcurrentDictionary<string, (string Id, string State)> _submitByKey = new();
        private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Version)> _tosByKey = new();

        public Task<KycSubmitResult> SubmitAsync(KycSubmitUpstreamPayload payload, string idempotencyKey, CancellationToken ct)
        {
            var replayed = _submitByKey.ContainsKey(idempotencyKey);
            var (id, state) = _submitByKey.GetOrAdd(idempotencyKey, _ => ("sub_" + Guid.NewGuid().ToString("N")[..12], "Submitted"));
            return Task.FromResult(new KycSubmitResult { SubmissionId = id, State = state, Replayed = replayed });
        }

        public Task<KycTosSignatureResult> StampTosSignatureAsync(string submissionId, KycTosStampPayload payload, string idempotencyKey, CancellationToken ct)
        {
            var (at, version) = _tosByKey.GetOrAdd(idempotencyKey, _ => (DateTimeOffset.UtcNow, payload.TosAcceptedVersion));
            return Task.FromResult(new KycTosSignatureResult { TosSignedAt = at, TosAcceptedVersion = version, Replayed = false });
        }

        private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Version)> _tosByUser = new();

        public Task<KycTosSignatureResult> StampStandaloneTosAsync(string userId, KycTosStampPayload payload, CancellationToken ct)
        {
            var (at, version) = _tosByUser.GetOrAdd(userId, _ => (DateTimeOffset.UtcNow, payload.TosAcceptedVersion));
            return Task.FromResult(new KycTosSignatureResult { TosSignedAt = at, TosAcceptedVersion = version, Replayed = false });
        }

        public Task<KycSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
            => Task.FromResult<KycSubmissionView?>(null);

        public Task<KycSubmissionView?> GetByIdAsync(string submissionId, CancellationToken ct)
            => Task.FromResult<KycSubmissionView?>(null);

        public Task<KycQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new KycQueuePage { Items = Array.Empty<KycSubmissionView>(), Page = page, PageSize = pageSize, Total = 0 });

        public Task<KycReviewDecision> ReviewAsync(string submissionId, KycReviewDecisionRequest request, CancellationToken ct)
            => Task.FromResult(new KycReviewDecision { SubmissionId = submissionId, Status = "Verified", GrantsRole = "jeeber" });
    }
}
