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

    // ----- JEBV4-258 NationalIdRegex hardening (^[0-9]{12}\z) -----
    // The national-ID shape rule must accept EXACTLY 12 ASCII digits — non-ASCII
    // Unicode digits (Arabic-Indic / Farsi) and a trailing newline are rejected.

    [Theory]
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢")] // Arabic-Indic ١٢٣٤٥٦٧٨٩٠١٢
    [InlineData("۱۲۳۴۵۶۷۸۹۰۱۲")] // Farsi ۱۲۳۴۵۶۷۸۹۰۱۲
    public async Task SubmitJson_NationalId_UnicodeDigits_Returns_400_PerJEBV4_258(string idNumber)
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor($"jebv4-258-unicode-{idNumber.GetHashCode():X}-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: "national_id", idNumber: idNumber), Guid.NewGuid().ToString("N"));

        // [0-9] (not \d) restricts to ASCII 0-9, so 12 non-ASCII Unicode digits no
        // longer satisfy the national-ID shape rule.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
    }

    [Fact]
    public async Task SubmitJson_NationalId_TrailingNewline_Returns_400_PerJEBV4_258()
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor("jebv4-258-trailing-newline-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: "national_id", idNumber: "123456789012\n"), Guid.NewGuid().ToString("N"));

        // \z (not $) anchors the true end-of-string, so "12 digits + \n" no longer
        // slips through the national-ID shape rule.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
    }

    [Fact]
    public async Task SubmitJson_NationalId_PlainTwelveAsciiDigits_Returns_201_PerJEBV4_258()
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor("jebv4-258-plain-ascii-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: "national_id", idNumber: "123456789012"), Guid.NewGuid().ToString("N"));

        // Regression guard: the hardening must NOT reject a valid 12 ASCII-digit ID.
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ----- E3 (owner decision Q-039: "Id number is a must") -----
    // id_number is REQUIRED for every id_type. (Per-id_type shape validation for
    // passport/residency was added later in JEBV4-256 — see the tests below.)

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
    [InlineData("residency")] // Q-042 ratified vocab
    public async Task SubmitJson_NonNationalId_Missing_IdNumber_Returns_400_PerE3(string idType)
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor($"e3-nonnational-{idType}-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: idType, idNumber: null), Guid.NewGuid().ToString("N"));

        // E3 (Q-039 — "Id number is a must") makes id_number mandatory for EVERY
        // id_type — superseding the pre-E3 JEBV4-113 §3.1 scoping that let
        // passport/residency submit with no id_number. The id_type itself is a
        // supported value (so the 400 is scoped to id_number, not id_type),
        // proving the passport/residency vocab is accepted.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
    }

    // ----- JEBV4-256: passport/residency id_number ARE shape-validated -----
    // The BFF now mirrors the form-builder jeeb_jeeber_v1 flavor patterns for
    // EVERY id_type, closing the gap where passport/residency accepted any
    // non-blank string (strictly more permissive than the client's own form):
    //   passport  -> ^[A-Z0-9]{6,9}$   (passport.json id_number)
    //   residency -> ^[A-Z0-9]{6,12}$  (residency.json id_number)

    [Theory]
    [InlineData("passport", "P1234567")]   // 8 chars, in 6-9 range
    [InlineData("passport", "ABC123")]     // 6 chars, lower bound
    [InlineData("passport", "AB1234567")]  // 9 chars, upper bound
    [InlineData("residency", "RP202455")]  // 8 chars, in 6-12 range
    [InlineData("residency", "RES001")]    // 6 chars, lower bound
    [InlineData("residency", "ABC123DEF456")] // 12 chars, upper bound
    public async Task SubmitJson_NonNationalId_With_Conforming_IdNumber_Returns_201_PerJEBV4_256(
        string idType, string idNumber)
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor($"jebv4-256-ok-{idType}-{idNumber}-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: idType, idNumber: idNumber), Guid.NewGuid().ToString("N"));

        // A shape-conforming passport/residency id_number clears the new per-type
        // gate and reaches 201 (regression guard: the tightening must not reject
        // values the form-builder pattern accepts).
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    // passport ^[A-Z0-9]{6,9}$
    [InlineData("passport", "AB123")]       // 5 chars, too short
    [InlineData("passport", "AB12345678")]  // 10 chars, too long
    [InlineData("passport", "RP-2024-5")]   // hyphen not in [A-Z0-9]
    [InlineData("passport", "abc1234")]     // lowercase not in [A-Z0-9]
    [InlineData("passport", "P1234567\n")]  // trailing newline (\z anchor)
    // residency ^[A-Z0-9]{6,12}$
    [InlineData("residency", "RES12")]           // 5 chars, too short
    [InlineData("residency", "ABC123DEF4567")]   // 13 chars, too long
    [InlineData("residency", "RP-2024-55")]      // hyphens not in [A-Z0-9]
    [InlineData("residency", "res00123")]        // lowercase not in [A-Z0-9]
    public async Task SubmitJson_NonNationalId_With_NonConforming_IdNumber_Returns_400_PerJEBV4_256(
        string idType, string idNumber)
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor($"jebv4-256-bad-{idType}-{idNumber.GetHashCode():X}-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: idType, idNumber: idNumber), Guid.NewGuid().ToString("N"));

        // A non-conforming passport/residency id_number now yields a field-scoped
        // 400 on id_number instead of silently reaching kyc-service — the
        // server-enforced contract matches the client-declared form contract.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
    }

    [Fact]
    public async Task SubmitJson_ResidencyPermit_Is_Not_A_Supported_IdType_Returns_400()
    {
        _factory.ContractSigning.Reset();
        var client = ClientFor("e3-residency-permit-rejected-user");
        var resp = await PostJsonAsync(
            client, "/v1/kyc/submit", Package(idType: "residency_permit", idNumber: "RP-2024-55"), Guid.NewGuid().ToString("N"));

        // E3 DoD (WORK-ORDER-2026-07-07 Lane E): the BFF enumerates EXACTLY
        // {national_id, passport, residency} — "no more" — and any other value
        // is rejected. "residency_permit" is NOT kept as an alias: no client
        // ever sent it (repo-wide search + form-builder flavors + mobile PR #80).
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_type");
        json.GetProperty("detail").GetString().Should().Contain("not a supported value");
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

        // The catalog returns the seeded Jeeb ToS template under its CANONICAL name
        // (jeeb_tos_v1 — the same name KycBffController resolves) so the AC8
        // cross-link resolves "v1" as the known version (and rejects
        // "unknown-tos-id"). Before JEBV4-197 the controller looked up a hyphenated
        // literal that matched no real catalog item, so the check silently never
        // fired; this fake now names the real template so the check is exercised.
        public Task<JsonElement> ListTemplatesAsync(CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(
                "{\"items\":[{\"template_id\":\"tmpl-tos-1\",\"name\":\"jeeb_tos_v1\",\"status\":\"ACTIVE\"}]}");
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
