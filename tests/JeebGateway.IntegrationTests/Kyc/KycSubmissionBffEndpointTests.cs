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
/// S03 / ADR-0004 — happy + negative coverage for the thin KYC JSON BFF
/// endpoints the gateway composes (DEC2 / DEC3): the ToS sign and the
/// submit-by-ref. The gateway holds ZERO KYC state — these run with
/// FeatureFlags:UseUpstream:Kyc ON against a FAKE IKycServiceClient standing in
/// for the owning kyc-service (the production path; there is no in-gateway
/// fallback). They pin the S03 console contract: snake_case fields, 201/200
/// idempotency served by the upstream replay flag, RFC7807 errors, no
/// signature-blob echo. Validation reds (empty blob, missing refs, anon) short
/// circuit BEFORE the seam and so hold regardless of the upstream.
/// </summary>
public sealed class KycSubmissionBffEndpointTests : IClassFixture<KycSubmissionBffEndpointTests.KycUpstreamFactory>
{
    private readonly KycUpstreamFactory _factory;

    public KycSubmissionBffEndpointTests(KycUpstreamFactory factory)
    {
        _factory = factory;
    }

    // ----- POST /v1/kyc/contract-template/sign (H5 / N1 / N10) -----

    [Fact]
    public async Task SignTos_Happy_Returns_200_With_Stamp_And_Does_Not_Echo_Blob()
    {
        var client = ClientFor("s03-sign-happy");
        var idem = Guid.NewGuid().ToString("N");

        var resp = await PostJsonAsync(client, "/v1/kyc/contract-template/sign", new
        {
            template_id = "tmpl-1",
            tos_version = "v1",
            signature_blob = "c2lnbmF0dXJl", // base64("signature")
        }, idem);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.TryGetProperty("tos_signed_at", out _).Should().BeTrue();
        json.GetProperty("tos_accepted_version").GetString().Should().Be("v1");
        // Must NOT echo the raw signature blob anywhere in the body.
        json.GetRawText().Should().NotContain("c2lnbmF0dXJl");
    }

    [Fact]
    public async Task SignTos_Empty_Blob_Returns_400_InvalidSignature_ProblemDetails()
    {
        var client = ClientFor("s03-sign-empty");

        var resp = await PostJsonAsync(client, "/v1/kyc/contract-template/sign", new
        {
            template_id = "tmpl-1",
            tos_version = "v1",
            signature_blob = "",
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("type").GetString().Should().Contain("invalid-signature");
    }

    [Fact]
    public async Task SignTos_Replay_Same_Idempotency_Key_Returns_Same_SignedAt_No_Double_Stamp()
    {
        var client = ClientFor("s03-sign-replay");
        var idem = Guid.NewGuid().ToString("N");
        var body = new { template_id = "tmpl-1", tos_version = "v1", signature_blob = "YmxvYg==" };

        var first = await PostJsonAsync(client, "/v1/kyc/contract-template/sign", body, idem);
        var firstStamp = (await ReadJsonAsync(first)).GetProperty("tos_signed_at").GetString();

        var replay = await PostJsonAsync(client, "/v1/kyc/contract-template/sign", body, idem);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayStamp = (await ReadJsonAsync(replay)).GetProperty("tos_signed_at").GetString();

        // N10: byte-for-byte equal — the upstream returns the original stamp on replay.
        replayStamp.Should().Be(firstStamp);
    }

    [Fact]
    public async Task SignTos_Without_Identity_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await PostJsonAsync(anon, "/v1/kyc/contract-template/sign", new
        {
            template_id = "tmpl-1",
            tos_version = "v1",
            signature_blob = "YmxvYg==",
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----- POST /v1/kyc/submit (H6 / N9) -----

    [Fact]
    public async Task SubmitJson_Happy_Returns_201_Submitted_With_Echoed_Refs()
    {
        var client = ClientFor("s03-submit-happy");
        var idem = Guid.NewGuid().ToString("N");

        var resp = await PostJsonAsync(client, "/v1/kyc/submit", SamplePackage(), idem);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("submissionId").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("state").GetString().Should().Be("Submitted");
        json.GetProperty("id_type").GetString().Should().Be("national_id");
        json.GetProperty("id_document_front_url").GetString().Should().Be("cdn://obj/front");
        json.GetProperty("selfie_with_liveness_url").GetString().Should().Be("cdn://obj/selfie");
    }

    [Fact]
    public async Task SubmitJson_Replay_Same_Idempotency_Key_Returns_200_Same_Id_No_Duplicate()
    {
        var client = ClientFor("s03-submit-replay");
        var idem = Guid.NewGuid().ToString("N");

        var first = await PostJsonAsync(client, "/v1/kyc/submit", SamplePackage(), idem);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstId = (await ReadJsonAsync(first)).GetProperty("submissionId").GetString();

        var replay = await PostJsonAsync(client, "/v1/kyc/submit", SamplePackage(), idem);

        // N9: replay returns 200 with the SAME submission id (no new row) — the
        // upstream signals the replay via the 200 vs 201 status.
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(replay)).GetProperty("submissionId").GetString().Should().Be(firstId);
    }

    [Fact]
    public async Task SubmitJson_Missing_Document_Refs_Returns_400_ProblemDetails()
    {
        var client = ClientFor("s03-submit-missing");

        var resp = await PostJsonAsync(client, "/v1/kyc/submit", new
        {
            id_type = "national_id",
            id_number = "123456789012",
            // all *_url refs omitted
            tos_accepted_version = "v1",
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("detail").GetString().Should().Contain("id_document_front_url");
    }

    // ----- E3 (owner decision Q-039): vehicle_registration_url relaxed -----

    [Fact]
    public async Task SubmitJson_No_Vehicle_Registration_Returns_201_E3_Relaxed()
    {
        var client = ClientFor("e3-no-vehicle");

        // The exact shape from the JEBV4-113 live evidence that used to 400 with
        // "the following document refs are required: vehicle_registration_url":
        // front + back + selfie + national_id + a valid 12-digit id_number, and
        // NO vehicle_registration_url. E3 removed vehicle from the KYC contract.
        var resp = await PostJsonAsync(client, "/v1/kyc/submit", new
        {
            id_type = "national_id",
            id_number = "123456789012",
            id_document_front_url = "cdn://obj/front",
            id_document_back_url = "cdn://obj/back",
            selfie_with_liveness_url = "cdn://obj/selfie",
            tos_accepted_version = "v1",
            // no vehicle_registration_url / vehicle_plate_number / vehicle_year_make_model
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("state").GetString().Should().Be("Submitted");
    }

    // ----- E3 (owner decision Q-039): id_type and id_number are REQUIRED -----

    [Fact]
    public async Task SubmitJson_Missing_IdType_Returns_400_Field_IdType()
    {
        var client = ClientFor("e3-missing-idtype");

        // All required document refs present, id_number present — only id_type
        // omitted. Pre-E3 the BFF treated id_type as optional and let this through.
        var resp = await PostJsonAsync(client, "/v1/kyc/submit", new
        {
            id_number = "123456789012",
            id_document_front_url = "cdn://obj/front",
            id_document_back_url = "cdn://obj/back",
            selfie_with_liveness_url = "cdn://obj/selfie",
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_type");
        json.GetProperty("detail").GetString().Should().Contain("id_type is required");
    }

    [Fact]
    public async Task SubmitJson_Missing_IdNumber_Returns_400_Field_IdNumber()
    {
        var client = ClientFor("e3-missing-idnumber");

        // All required document refs present, id_type present — only id_number
        // omitted. E3 ("Id number is a must") requires it for every id_type.
        var resp = await PostJsonAsync(client, "/v1/kyc/submit", new
        {
            id_type = "national_id",
            id_document_front_url = "cdn://obj/front",
            id_document_back_url = "cdn://obj/back",
            selfie_with_liveness_url = "cdn://obj/selfie",
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("field").GetString().Should().Be("id_number");
        json.GetProperty("detail").GetString().Should().Contain("id_number is required");
    }

    // ----- Q-042 vocab: residency (canonical) + residency_permit (alias) accepted -----

    [Theory]
    [InlineData("residency")]        // Q-042 ratified/canonical value
    [InlineData("residency_permit")] // retained back-compat alias
    public async Task SubmitJson_Residency_Vocab_Accepted_Returns_201(string idType)
    {
        var client = ClientFor($"q042-{idType}");

        var resp = await PostJsonAsync(client, "/v1/kyc/submit", new
        {
            id_type = idType,
            id_number = "RP-2024-77", // free-form (non-national), just present
            id_document_front_url = "cdn://obj/front",
            id_document_back_url = "cdn://obj/back",
            selfie_with_liveness_url = "cdn://obj/selfie",
        }, Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SubmitJson_Without_Identity_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await PostJsonAsync(anon, "/v1/kyc/submit", SamplePackage(), Guid.NewGuid().ToString("N"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----- helpers -----

    private static object SamplePackage() => new
    {
        id_type = "national_id",
        id_number = "123456789012",
        id_document_front_url = "cdn://obj/front",
        id_document_back_url = "cdn://obj/back",
        driver_license_number = "DL-99887766",
        driver_license_expiry = "2030-01-01",
        vehicle_registration_url = "cdn://obj/vehreg",
        vehicle_plate_number = "ABC-1234",
        vehicle_year_make_model = "2021 Toyota Corolla",
        selfie_with_liveness_url = "cdn://obj/selfie",
        tos_accepted_version = "v1",
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
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Boots the gateway with FeatureFlags:UseUpstream:Kyc ON and a FAKE
    /// IKycServiceClient registered in place of the HttpClient-backed one. This
    /// exercises the PRODUCTION seam path (KycBffSeam -> IKycServiceClient) without
    /// a network kyc-service. The fake honours the idempotency contract (replay
    /// returns the same id / stamp + signals replay) so N9/N10 are real, not faked.
    /// </summary>
    public sealed class KycUpstreamFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Kyc", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKycServiceClient>();
                services.AddSingleton<IKycServiceClient, FakeKycServiceClient>();
            });
        }
    }

    /// <summary>
    /// In-test stand-in for the owning kyc-service. NOT shipped in the gateway —
    /// it lives only in the test assembly. Honours Idempotency-Key dedup so the
    /// replay assertions (N9/N10) exercise the real seam wiring.
    /// </summary>
    private sealed class FakeKycServiceClient : IKycServiceClient
    {
        private readonly ConcurrentDictionary<string, (string Id, string State)> _submitByKey = new();
        private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Version)> _tosByKey = new();
        private readonly ConcurrentDictionary<string, KycSubmissionView> _byId = new();

        public Task<KycSubmitResult> SubmitAsync(KycSubmitUpstreamPayload payload, string idempotencyKey, CancellationToken ct)
        {
            var replayed = _submitByKey.ContainsKey(idempotencyKey);
            var (id, state) = _submitByKey.GetOrAdd(idempotencyKey, _ => ("sub_" + Guid.NewGuid().ToString("N")[..12], "Submitted"));
            _byId[id] = new KycSubmissionView
            {
                SubmissionId = id,
                UserId = payload.UserId,
                Status = state,
                SubmittedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(new KycSubmitResult
            {
                SubmissionId = id,
                State = state,
                Replayed = replayed,
            });
        }

        public Task<KycTosSignatureResult> StampTosSignatureAsync(string submissionId, KycTosStampPayload payload, string idempotencyKey, CancellationToken ct)
        {
            var replayed = _tosByKey.ContainsKey(idempotencyKey);
            var (at, version) = _tosByKey.GetOrAdd(idempotencyKey, _ => (DateTimeOffset.UtcNow, payload.TosAcceptedVersion));
            return Task.FromResult(new KycTosSignatureResult
            {
                TosSignedAt = at,
                TosAcceptedVersion = version,
                Replayed = replayed,
            });
        }

        private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Version)> _tosByUser = new();

        public Task<KycTosSignatureResult> StampStandaloneTosAsync(string userId, KycTosStampPayload payload, CancellationToken ct)
        {
            // Idempotent on subject: first sign records now(); replay returns the original.
            var (at, version) = _tosByUser.GetOrAdd(userId, _ => (DateTimeOffset.UtcNow, payload.TosAcceptedVersion));
            return Task.FromResult(new KycTosSignatureResult
            {
                TosSignedAt = at,
                TosAcceptedVersion = version,
                Replayed = false,
            });
        }

        public Task<KycSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
        {
            KycSubmissionView? found = _byId.Values.FirstOrDefault(v => v.UserId == userId);
            return Task.FromResult(found);
        }

        public Task<KycSubmissionView?> GetByIdAsync(string submissionId, CancellationToken ct)
            => Task.FromResult(_byId.TryGetValue(submissionId, out var v) ? v : null);

        public Task<KycQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new KycQueuePage { Items = Array.Empty<KycSubmissionView>(), Page = page, PageSize = pageSize, Total = 0 });

        public Task<KycReviewDecision> ReviewAsync(string submissionId, KycReviewDecisionRequest request, CancellationToken ct)
            => Task.FromResult(new KycReviewDecision
            {
                SubmissionId = submissionId,
                Status = request.Action == KycReviewActionKind.Approve ? "Verified" : "Rejected",
                GrantsRole = request.Action == KycReviewActionKind.Approve ? "jeeber" : null,
            });
    }
}
