using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S04 voice-on-create slice (JEB-1431) through the REAL ASP.NET pipeline. The
/// upstream voice service is replaced by a deterministic stub so these tests prove
/// the GATEWAY behaviour (multipart parse, role/size/format/cap gates, requestId->
/// row id, idempotent re-submit, response shape) without a network dependency. The
/// transcript VALUE provenance (real Whisper vs gated mock) is the owning service's
/// concern, covered in voice-transcription-service's own suite.
/// </summary>
public class RequestVoiceEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestVoiceEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVoiceTranscriptionClient>(new StubVoiceClient());
                services.Configure<UpstreamFeatureFlags>(f => f.Voice = true);
            });
        });
    }

    private HttpClient ClientFor(string userId, string roles = "customer")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", roles);
        return client;
    }

    private static MultipartFormDataContent VoiceForm(byte[] audio, string requestId, string contentType = "audio/wav", string filename = "ar-5s.wav")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(audio);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "audio", filename);
        form.Add(new StringContent(requestId), "requestId");
        form.Add(new StringContent("standard"), "tier");
        return form;
    }

    [Fact] // H1
    public async Task Voice_Create_Returns_201_With_Transcript_Confidence_Language()
    {
        var client = ClientFor("s04-h1");
        var reqId = Guid.NewGuid().ToString();

        var resp = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1, 2, 3 }, reqId));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<VoiceResp>();
        body!.RequestId.Should().Be(reqId);
        body.Transcription.Should().NotBeNullOrWhiteSpace();
        body.TranscriptionConfidence.Should().BeInRange(0.0, 1.0);
        body.Language.Should().Be("ar");
    }

    [Fact] // H2
    public async Task Voice_ReadBack_Echoes_Same_Transcript_And_Confidence()
    {
        var client = ClientFor("s04-h2");
        var reqId = Guid.NewGuid().ToString();
        var create = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, reqId));
        var created = await create.Content.ReadFromJsonAsync<VoiceResp>();

        // JEBV4-61: the voice-specific echo (transcription/confidence/language)
        // now lives on its own non-colliding route; GET /v1/requests/{id} is
        // solely owned by JeebRequestsController.Get (canonical DeliveryRequestDto).
        var read = await client.GetAsync($"/v1/requests/{reqId}/voice");

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await read.Content.ReadFromJsonAsync<VoiceResp>();
        body!.RequestId.Should().Be(reqId);
        body.Transcription.Should().Be(created!.Transcription);
        body.TranscriptionConfidence.Should().Be(created.TranscriptionConfidence);
        body.Language.Should().Be("ar");
    }

    [Fact] // A1
    public async Task Voice_Idempotent_Resubmit_Returns_200_No_Duplicate()
    {
        var client = ClientFor("s04-a1");
        var reqId = Guid.NewGuid().ToString();
        var first = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, reqId));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadFromJsonAsync<VoiceResp>();

        var second = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, reqId));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<VoiceResp>();
        secondBody!.Id.Should().Be(firstBody!.Id);
        secondBody.Transcription.Should().Be(firstBody.Transcription);
    }

    [Fact] // N1
    public async Task Voice_Oversize_Audio_Returns_413_No_Row()
    {
        var client = ClientFor("s04-n1");
        var big = new byte[5 * 1024 * 1024 + 1];

        var resp = await client.PostAsync("/v1/requests", VoiceForm(big, Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact] // N3
    public async Task Voice_Unsupported_Format_Returns_415()
    {
        var client = ClientFor("s04-n3");

        var resp = await client.PostAsync("/v1/requests",
            VoiceForm(new byte[] { 1 }, Guid.NewGuid().ToString(), contentType: "audio/aac", filename: "clip.aac"));

        resp.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact] // N7
    public async Task Voice_No_Token_Returns_401()
    {
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // N8
    public async Task Voice_Wrong_Role_Driver_Returns_403()
    {
        var driver = ClientFor("s04-n8-omar", roles: "driver");

        var resp = await driver.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // N9
    public async Task Voice_Over_BR9_Cap_Returns_409()
    {
        var client = ClientFor("s04-n9-capped");
        // Seed 3 real active requests via the typed path -> reach the BR-9 cap (3).
        for (var i = 0; i < 3; i++)
        {
            var seed = await client.PostAsJsonAsync("/requests", new
            {
                description = $"cap seed {i}",
                tierId = "flash",
                pickupLocation = new { lat = 24.7, lng = 46.6 },
                dropoffLocation = new { lat = 24.6, lng = 46.7 }
            });
            seed.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var resp = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, Guid.NewGuid().ToString()));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact] // N9-reverse: cross-surface cap symmetry — voice creates count toward the
           // cap a subsequent typed /requests create checks. Locks the shared-store
           // invariant the live SETUP-5/6/7 -> N9 leg depends on, in the inverse order.
    public async Task Three_Voice_Creates_Then_Typed_Request_Returns_409()
    {
        var client = ClientFor("s04-n9-voice-first");

        // Seed 3 real active requests via the VOICE path -> reach the BR-9 cap (3).
        for (var i = 0; i < 3; i++)
        {
            var seed = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1 }, Guid.NewGuid().ToString()));
            seed.StatusCode.Should().Be(HttpStatusCode.Created, $"voice cap-seed {i} should succeed under the cap");
        }

        // The 4th create on the TYPED surface must trip the same shared cap -> 409.
        var blocked = await client.PostAsJsonAsync("/requests", new
        {
            description = "typed after 3 voice",
            tierId = "flash",
            pickupLocation = new { lat = 24.7, lng = 46.6 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });

        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await blocked.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.Conflict);
    }

    // ----- JEBV4-61: GET /v1/requests/{id} route-collision fix -----
    //
    // Before the fix, RequestVoiceController.GetVoiceById mapped GET v1/requests/{id}
    // at implicit Order = 0 and JeebRequestsController.Get mapped the SAME route at
    // an explicit Order = 1, so the narrow VoiceRequestResponse shape won for EVERY
    // request (voice-created or not) and the full DeliveryRequestDto action never
    // ran — jeeberId/conversationId/pickup/dropoff locations were silently absent
    // from a clean 200. This test pins the canonical shape for BOTH creation paths.

    [Fact]
    public async Task GetById_Returns_Canonical_DeliveryRequestDto_For_Voice_Created_Request()
    {
        var client = ClientFor("jebv4-61-voice");
        var reqId = Guid.NewGuid().ToString();
        var create = await client.PostAsync("/v1/requests", VoiceForm(new byte[] { 1, 2, 3 }, reqId));
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var read = await client.GetAsync($"/v1/requests/{reqId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Canonical DeliveryRequestDto fields the voice shape used to swallow.
        root.GetProperty("id").GetString().Should().Be(reqId);
        root.GetProperty("clientId").GetString().Should().Be("jebv4-61-voice");
        root.TryGetProperty("jeeberId", out _).Should().BeTrue(
            "the full DTO carries jeeberId (even when null) — the field this bug swallowed");
        root.TryGetProperty("conversationId", out _).Should().BeTrue(
            "the full DTO carries conversationId (even when null) — the field this bug swallowed");
        root.TryGetProperty("pickupLocation", out var pickup).Should().BeTrue();
        pickup.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        root.TryGetProperty("dropoffLocation", out _).Should().BeTrue();
        root.TryGetProperty("createdAt", out _).Should().BeTrue();
        root.TryGetProperty("photos", out _).Should().BeTrue();
        root.GetProperty("transcription").GetString().Should().NotBeNullOrWhiteSpace(
            "DeliveryRequestDto.Transcription is populated even for a voice-created row");

        // Distinguishing markers of the OLD narrow VoiceRequestResponse shape that must
        // NOT appear on the canonical route anymore (that shape lives at .../voice now).
        root.TryGetProperty("requestId", out _).Should().BeFalse(
            "requestId is a VoiceRequestResponse-only field; must not leak onto the canonical route");
        root.TryGetProperty("transcription_confidence", out _).Should().BeFalse(
            "transcription_confidence is a VoiceRequestResponse-only field; belongs on GET .../voice");
        root.TryGetProperty("language", out _).Should().BeFalse(
            "language is a VoiceRequestResponse-only field; belongs on GET .../voice");
    }

    [Fact]
    public async Task GetById_Returns_Canonical_DeliveryRequestDto_For_Json_Created_Request()
    {
        var client = ClientFor("jebv4-61-json");
        var createResp = await client.PostAsJsonAsync("/v1/requests", new
        {
            description = "Two large pizzas, ring the top bell",
            tierId = "standard",
            pickupLocation = new { lat = 24.7, lng = 46.6 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 },
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var reqId = createdDoc.RootElement.GetProperty("id").GetString();

        var read = await client.GetAsync($"/v1/requests/{reqId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("id").GetString().Should().Be(reqId);
        root.GetProperty("clientId").GetString().Should().Be("jebv4-61-json");
        root.GetProperty("description").GetString().Should().Be("Two large pizzas, ring the top bell");
        root.GetProperty("pickupLocation").GetProperty("lat").GetDouble().Should().BeApproximately(24.7, 0.0001);
        root.GetProperty("dropoffLocation").GetProperty("lng").GetDouble().Should().BeApproximately(46.7, 0.0001);
        root.TryGetProperty("jeeberId", out _).Should().BeTrue();
        root.TryGetProperty("conversationId", out _).Should().BeTrue();
        root.TryGetProperty("createdAt", out _).Should().BeTrue();

        root.TryGetProperty("requestId", out _).Should().BeFalse();
        root.TryGetProperty("transcription_confidence", out _).Should().BeFalse();
        root.TryGetProperty("language", out _).Should().BeFalse();
    }

    private sealed record VoiceResp(
        [property: System.Text.Json.Serialization.JsonPropertyName("requestId")] string RequestId,
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("transcription")] string? Transcription,
        [property: System.Text.Json.Serialization.JsonPropertyName("transcription_confidence")] double? TranscriptionConfidence,
        [property: System.Text.Json.Serialization.JsonPropertyName("language")] string Language,
        [property: System.Text.Json.Serialization.JsonPropertyName("tierId")] string? TierId);

    /// <summary>Deterministic upstream stub — returns a fixed transcript + confidence.</summary>
    private sealed class StubVoiceClient : IVoiceTranscriptionClient
    {
        public Task<TranscriptionResult> TranscribeAsync(WhisperAudio audio, string language, CancellationToken ct)
            => TranscribeVoiceAsync(audio, language, null, ct);

        public Task<TranscriptionResult> TranscribeVoiceAsync(WhisperAudio audio, string language, string? idempotencyKey, CancellationToken ct)
            => Task.FromResult(new TranscriptionResult(
                AudioId: Guid.NewGuid().ToString("n"),
                Outcome: TranscriptionOutcome.Transcribed,
                Transcription: new WhisperTranscription("كيلو بندورة من السوق", language, 0.93),
                Reason: null));
    }
}
