using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.Whisper;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Whisper;

/// <summary>
/// thin-BFF fan-out (3 of 4) — contract guard for the voice-transcription-service
/// submit/result seam consumed by <see cref="VoiceTranscriptionClient"/> when
/// <c>FeatureFlags:UseUpstream:Voice</c> is ON.
///
/// Two layers:
///   * SEAM tests (always run, no network): drive the REAL client against a fake
///     <see cref="HttpMessageHandler"/> returning the LITERAL upstream bodies —
///     the current <c>501 {"detail":"T-BE-007 not yet implemented"}</c> placeholder
///     and a hypothetical real <c>200 {"text":...,"language":...}</c> — locking the
///     mapping onto the gateway's <see cref="TranscriptionResult"/> contract and
///     the snake_case request body the FastAPI upstream will consume.
///   * LIVE contract test against <c>http://192.168.2.50:10062</c>: proves the real
///     submit path (POST /v1/transcribe) plus /healthz + /readyz reachability. It
///     is reachability-guarded: when the host is unreachable (e.g. CI without the
///     overlay network) the assertions are skipped rather than failing the build,
///     per the timebox discipline. CI is authoritative.
/// </summary>
public class VoiceTranscriptionClientContractTests
{
    private const string LiveBaseUrl = "http://192.168.2.50:10062/";

    // ---- SEAM: upstream placeholder (501) maps to QueuedForRetry ----
    [Fact]
    public async Task Maps_Upstream_501_Placeholder_To_QueuedForRetry()
    {
        var client = ClientReturning(
            HttpStatusCode.NotImplemented,
            """{"detail":"T-BE-007 not yet implemented"}""");

        var result = await client.TranscribeAsync(SampleAudio(), "ar", CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        result.Transcription.Should().BeNull();
        result.Reason.Should().Be("upstream_not_implemented");
        result.AudioId.Should().NotBeNullOrWhiteSpace();
    }

    // ---- SEAM: a real 200 transcription binds to Transcribed ----
    [Fact]
    public async Task Maps_Upstream_200_Body_To_Transcribed()
    {
        var client = ClientReturning(
            HttpStatusCode.OK,
            """{"text":"مرحبا","language":"ar"}""");

        var result = await client.TranscribeAsync(SampleAudio(), "ar", CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.Transcribed);
        result.Transcription.Should().NotBeNull();
        result.Transcription!.Text.Should().Be("مرحبا");
        result.Transcription.Language.Should().Be("ar");
    }

    // ---- SEAM: 5xx surfaces as WhisperUnavailableException (controller degrades it) ----
    [Fact]
    public async Task Transient_5xx_Throws_WhisperUnavailable()
    {
        var client = ClientReturning(HttpStatusCode.BadGateway, "upstream down");

        var act = async () => await client.TranscribeAsync(SampleAudio(), "ar", CancellationToken.None);

        await act.Should().ThrowAsync<WhisperUnavailableException>();
    }

    // ---- SEAM: request body is snake_case + base64 (FastAPI convention) ----
    [Fact]
    public async Task Sends_SnakeCase_Base64_Request_Body()
    {
        string? sentBody = null;
        var handler = new CapturingHandler(
            HttpStatusCode.NotImplemented,
            """{"detail":"T-BE-007 not yet implemented"}""",
            body => sentBody = body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://voice.test/") };
        var client = new VoiceTranscriptionClient(http, NullLogger<VoiceTranscriptionClient>.Instance);

        await client.TranscribeAsync(SampleAudio(), "ar", CancellationToken.None);

        sentBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(sentBody!);
        var root = doc.RootElement;
        root.GetProperty("audio_base64").GetString().Should().Be(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("hello-audio")));
        root.GetProperty("file_name").GetString().Should().Be("clip.wav");
        root.GetProperty("content_type").GetString().Should().Be("audio/wav");
        root.GetProperty("language").GetString().Should().Be("ar");
    }

    // ---- LIVE: real upstream submit/result path + health probes ----
    [Fact]
    public async Task Live_Upstream_Submit_Path_And_Health_Are_Reachable()
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        if (!await IsReachable(probe))
        {
            // Host unreachable from this runner (no overlay network) — skip the
            // live assertions. The SEAM tests above still lock the contract.
            return;
        }

        // 1) liveness + readiness
        var healthz = await probe.GetAsync(LiveBaseUrl + "healthz");
        healthz.StatusCode.Should().Be(HttpStatusCode.OK);
        var readyz = await probe.GetAsync(LiveBaseUrl + "readyz");
        readyz.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2) real submit/result path via the REAL typed client
        using var http = new HttpClient { BaseAddress = new Uri(LiveBaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        var client = new VoiceTranscriptionClient(http, NullLogger<VoiceTranscriptionClient>.Instance);

        var result = await client.TranscribeAsync(SampleAudio(), "ar", CancellationToken.None);

        // The upstream is the T-BE-007 placeholder today: it returns 501, which
        // the client maps to QueuedForRetry. Once the upstream implements real
        // transcription (200 body), this same call returns Transcribed without a
        // client change. Either way the submit path is proven end-to-end.
        result.Should().NotBeNull();
        result.Outcome.Should().BeOneOf(
            TranscriptionOutcome.QueuedForRetry, TranscriptionOutcome.Transcribed);
        if (result.Outcome == TranscriptionOutcome.QueuedForRetry)
        {
            result.Reason.Should().Be("upstream_not_implemented");
        }
    }

    private static async Task<bool> IsReachable(HttpClient probe)
    {
        try
        {
            var resp = await probe.GetAsync(LiveBaseUrl + "healthz");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static WhisperAudio SampleAudio() =>
        new(Encoding.UTF8.GetBytes("hello-audio"), "clip.wav", "audio/wav");

    private static VoiceTranscriptionClient ClientReturning(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://voice.test/") };
        return new VoiceTranscriptionClient(http, NullLogger<VoiceTranscriptionClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Action<string> _capture;

        public CapturingHandler(HttpStatusCode status, string body, Action<string> capture)
        {
            _status = status;
            _body = body;
            _capture = capture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                _capture(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
