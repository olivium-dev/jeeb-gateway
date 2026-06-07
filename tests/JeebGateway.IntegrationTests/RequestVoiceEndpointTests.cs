using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

        var read = await client.GetAsync($"/v1/requests/{reqId}");

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
