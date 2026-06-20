using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Whisper;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Thin HTTP proxy over voice-transcription-service <c>POST /v1/transcribe</c>.
///
/// The upstream's canonical contract route is <c>multipart/form-data</c>
/// (a FastAPI <c>UploadFile</c> field <c>audio</c> + a <c>Form</c> field
/// <c>language_hint</c>), returning
/// <c>{ transcript, confidence, language, duration_ms }</c>. Posting a JSON
/// envelope is rejected by FastAPI with <c>422</c> (missing required form field
/// <c>audio</c>), which the gateway then surfaces as a <c>502</c> — this was
/// E2E gap 2.3. Both <see cref="TranscribeAsync"/> and
/// <see cref="TranscribeVoiceAsync"/> therefore post the audio as multipart.
/// This client:
///   * binds the canonical 200 body into <see cref="TranscriptionResult"/> with
///     outcome <c>Transcribed</c>,
///   * maps an upstream <c>422 unprocessable_audio</c> (supported format but
///     empty/silent/too-short/corrupt audio) onto outcome <c>QueuedForRetry</c>
///     (a client-side degrade, NOT an outage — never a 502), and
///   * lets transport faults / 5xx surface through the resilience pipeline
///     (retry → circuit breaker) attached in
///     <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>; a final
///     transport failure throws <see cref="WhisperUnavailableException"/>.
/// Response binding stays snake_case to match the FastAPI convention used
/// across the fleet's Python services.
/// </summary>
public sealed class VoiceTranscriptionClient : IVoiceTranscriptionClient
{
    // FastAPI services in the fleet serialize snake_case; keep the request and
    // response binding snake_case to avoid a silent contract drift the moment
    // the upstream implements T-BE-007 and starts returning a real body.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger<VoiceTranscriptionClient> _log;

    public VoiceTranscriptionClient(HttpClient http, ILogger<VoiceTranscriptionClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<TranscriptionResult> TranscribeAsync(WhisperAudio audio, string language, CancellationToken ct)
    {
        // The upstream's canonical contract route POST /v1/transcribe is
        // multipart/form-data (FastAPI UploadFile `audio` + Form `language_hint`),
        // returning {transcript, confidence, language, duration_ms}. A JSON
        // envelope is rejected with 422 (missing required form field `audio`),
        // which the gateway then surfaces as a 502 (gap 2.3). We post the audio
        // as multipart to match the live contract.
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio.Content);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(audio.ContentType) ? "application/octet-stream" : audio.ContentType);
        form.Add(fileContent, "audio", string.IsNullOrWhiteSpace(audio.FileName) ? "audio.bin" : audio.FileName);
        form.Add(new StringContent(language), "language_hint");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/transcribe")
        {
            Content = form
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WhisperUnavailableException("voice-transcription request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new WhisperUnavailableException("voice-transcription transport failure", ex);
        }

        using var _ = response;

        // 422 unprocessable_audio is a CLIENT outcome (supported format but
        // empty/silent/too-short/corrupt audio). It is NOT an upstream outage,
        // so do NOT throw (which the controller maps to a 502). Degrade to the
        // same queued shape used for other non-transcribed outcomes — the
        // gateway accepted the audio, it simply could not be transcribed.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _log.LogInformation(
                "voice-transcription-service returned 422 (unprocessable audio); deferring transcription");
            return new TranscriptionResult(
                AudioId: Guid.NewGuid().ToString("n"),
                Outcome: TranscriptionOutcome.QueuedForRetry,
                Transcription: null,
                Reason: "unprocessable_audio");
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // Let the controller-level decision treat this as unavailable; the
            // resilience pipeline already retried transient statuses.
            throw new WhisperUnavailableException(
                $"voice-transcription returned transient status {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<V1VoiceUpstreamResponse>(Json, ct);
        var text = body?.Transcript ?? string.Empty;
        return new TranscriptionResult(
            AudioId: Guid.NewGuid().ToString("n"),
            Outcome: TranscriptionOutcome.Transcribed,
            Transcription: new WhisperTranscription(text, body?.Language ?? language, body?.Confidence),
            Reason: null);
    }

    /// <summary>
    /// Voice-on-create (JEB-1431). Forwards the audio to the upstream's canonical
    /// multipart route <c>POST /v1/transcribe</c> (the single stable contract route
    /// pinned by JEB-1483, returning <c>{transcript, confidence, language,
    /// duration_ms}</c>, backed by the owning service's real Whisper or its
    /// config-gated mock seam). The legacy <c>/v1/voice/transcribe</c> alias is now
    /// deprecated upstream (JEB-1482), so the gateway consumes the canonical path
    /// only. The gateway requestId is mapped to the generic <c>Idempotency-Key</c>
    /// header so a retried draft replays the cached transcript. Size (413) / format
    /// (415) gates are validated at the gateway BEFORE this call (so no Whisper runs
    /// on reject); this method also surfaces any upstream 413/415 onto a typed
    /// exception.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeVoiceAsync(
        WhisperAudio audio, string language, string? idempotencyKey, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio.Content);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(audio.ContentType) ? "application/octet-stream" : audio.ContentType);
        form.Add(fileContent, "audio", audio.FileName);
        form.Add(new StringContent(language), "language_hint");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/transcribe")
        {
            Content = form
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WhisperUnavailableException("voice-transcription request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new WhisperUnavailableException("voice-transcription transport failure", ex);
        }

        using var _ = response;

        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            throw new VoiceAudioRejectedException(413, "audio_too_large");
        }
        if (response.StatusCode == HttpStatusCode.UnsupportedMediaType)
        {
            throw new VoiceAudioRejectedException(415, "unsupported_format");
        }
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new WhisperUnavailableException(
                $"voice-transcription returned transient status {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<V1VoiceUpstreamResponse>(Json, ct);
        var text = body?.Transcript ?? string.Empty;
        return new TranscriptionResult(
            AudioId: Guid.NewGuid().ToString("n"),
            Outcome: TranscriptionOutcome.Transcribed,
            Transcription: new WhisperTranscription(text, body?.Language ?? language, body?.Confidence),
            Reason: null);
    }

    // Canonical /v1/transcribe response: {transcript, confidence, language, duration_ms}.
    private sealed record V1VoiceUpstreamResponse(
        [property: JsonPropertyName("transcript")] string? Transcript,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("duration_ms")] int? DurationMs);
}

/// <summary>
/// Raised when the upstream voice service rejects the audio with a typed client
/// error (413 too large / 415 unsupported format). The gateway voice slice maps
/// this straight onto the same HTTP status so the reject surfaces before any
/// request row is created — no Whisper, no draft.
/// </summary>
public sealed class VoiceAudioRejectedException : Exception
{
    public int StatusCode { get; }
    public string Reason { get; }

    public VoiceAudioRejectedException(int statusCode, string reason)
        : base($"voice audio rejected ({statusCode}): {reason}")
    {
        StatusCode = statusCode;
        Reason = reason;
    }
}
