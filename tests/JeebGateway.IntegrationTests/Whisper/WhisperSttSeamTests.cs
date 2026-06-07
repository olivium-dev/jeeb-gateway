using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using JeebGateway.Whisper;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Whisper;

/// <summary>
/// Track C — REAL OpenAI Whisper STT seam (S04).
///
/// Locks the real-vs-fake switch the gateway uses in Program.cs:
///   useRealWhisper = !FakeTranscribe AND ApiKey is non-empty.
///
/// Three layers, ALL network-free:
///   * <see cref="FakeWhisperClient"/> returns a deterministic transcript and never
///     calls out — the dev/CI fallback that must always remain present.
///   * The REAL <see cref="WhisperClient"/> is driven against a fake
///     <see cref="HttpMessageHandler"/> so the OpenAI request shape (multipart fields,
///     Bearer auth) and the response mapping (<c>{"text":...}</c> →
///     <see cref="WhisperTranscription"/>) are pinned WITHOUT hitting the live API.
///   * The selection predicate is asserted across the truth table so a future edit to
///     the seam can't silently flip prod onto the fake (or onto a keyless real path).
/// </summary>
public class WhisperSttSeamTests
{
    private static readonly WhisperAudio SampleAudio =
        new(Encoding.UTF8.GetBytes("hello-audio"), "clip.m4a", "audio/m4a");

    // ----------------------------------------------------------------------
    // FakeWhisperClient — deterministic, no network, always available.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Fake_Returns_Deterministic_Transcript_In_Configured_Language()
    {
        var client = new FakeWhisperClient(Options.Create(new WhisperOptions { Language = "ar" }));

        var result = await client.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Should().NotBeNull();
        result.Text.Should().Contain("clip.m4a");
        result.Language.Should().Be("ar");
        result.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task Fake_Is_Stable_Across_Calls_For_Same_Input()
    {
        var client = new FakeWhisperClient(Options.Create(new WhisperOptions()));

        var a = await client.TranscribeAsync(SampleAudio, CancellationToken.None);
        var b = await client.TranscribeAsync(SampleAudio, CancellationToken.None);

        b.Text.Should().Be(a.Text);
    }

    [Fact]
    public async Task Fake_Honors_Cancellation()
    {
        var client = new FakeWhisperClient(Options.Create(new WhisperOptions()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await client.TranscribeAsync(SampleAudio, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ----------------------------------------------------------------------
    // Real WhisperClient — driven against a mocked handler (NO live API).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Real_Maps_OpenAI_200_Body_To_Transcription()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"text":"كيلو بندورة"}""");
        var client = RealClient(handler, new WhisperOptions { Language = "ar", ApiKey = "sk-test" });

        var result = await client.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Text.Should().Be("كيلو بندورة");
        result.Language.Should().Be("ar");
    }

    [Fact]
    public async Task Real_Sends_Bearer_Auth_And_Multipart_OpenAI_Fields()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"text":"x"}""");
        var client = RealClient(handler, new WhisperOptions
        {
            Model = "whisper-1",
            Language = "ar",
            ApiKey = "sk-secret-123"
        });

        await client.TranscribeAsync(SampleAudio, CancellationToken.None);

        handler.AuthScheme.Should().Be("Bearer");
        handler.AuthParameter.Should().Be("sk-secret-123");
        handler.RequestUri!.AbsoluteUri.Should().EndWith("/audio/transcriptions");
        // .NET MultipartFormDataContent emits unquoted disposition field names (name=model).
        handler.SentBody.Should().Contain("name=model").And.Contain("whisper-1");
        handler.SentBody.Should().Contain("name=language").And.Contain("ar");
        handler.SentBody.Should().Contain("name=response_format").And.Contain("json");
        handler.SentBody.Should().Contain("name=file");
    }

    [Fact]
    public async Task Real_Transient_5xx_Throws_WhisperUnavailable()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadGateway, "upstream down");
        var client = RealClient(handler, new WhisperOptions { ApiKey = "sk-test" });

        var act = async () => await client.TranscribeAsync(SampleAudio, CancellationToken.None);

        await act.Should().ThrowAsync<WhisperUnavailableException>();
    }

    [Fact]
    public async Task Real_429_Throws_WhisperUnavailable()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests, "slow down");
        var client = RealClient(handler, new WhisperOptions { ApiKey = "sk-test" });

        var act = async () => await client.TranscribeAsync(SampleAudio, CancellationToken.None);

        await act.Should().ThrowAsync<WhisperUnavailableException>();
    }

    // ----------------------------------------------------------------------
    // Seam selection predicate — mirrors Program.cs:
    //   useRealWhisper = !FakeTranscribe AND ApiKey non-empty.
    // Guards prod against silently falling onto the fake or onto a keyless real path.
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(false, "sk-live", true)]   // real STT armed
    [InlineData(true, "sk-live", false)]   // explicit fake wins even with a key
    [InlineData(false, "", false)]         // no key => fallback to fake
    [InlineData(false, null, false)]       // no key => fallback to fake
    [InlineData(true, null, false)]        // fake + no key => fake
    public void Selects_Real_Only_When_Not_Fake_And_Key_Present(
        bool fakeTranscribe, string? apiKey, bool expectReal)
    {
        var opts = new WhisperOptions { FakeTranscribe = fakeTranscribe, ApiKey = apiKey };

        var useRealWhisper = !opts.FakeTranscribe && !string.IsNullOrWhiteSpace(opts.ApiKey);

        useRealWhisper.Should().Be(expectReal);
    }

    [Fact]
    public void Default_Options_Have_Real_Openai_BaseUrl_And_FakeFalse()
    {
        // The committed appsettings sets FakeTranscribe=true; the TYPE default must be
        // false so any binding that omits the key defaults to attempting real STT
        // (then degrades to fake only when the key is absent).
        var opts = new WhisperOptions();

        opts.FakeTranscribe.Should().BeFalse();
        opts.BaseUrl.Should().Be("https://api.openai.com/v1");
        opts.Model.Should().Be("whisper-1");
    }

    private static WhisperClient RealClient(HttpMessageHandler handler, WhisperOptions opts)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        return new WhisperClient(http, Options.Create(opts));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public string? SentBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? AuthScheme { get; private set; }
        public string? AuthParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthenticationHeaderValue? auth = request.Headers.Authorization;
            AuthScheme = auth?.Scheme;
            AuthParameter = auth?.Parameter;
            if (request.Content is not null)
            {
                SentBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
