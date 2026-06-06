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
/// The upstream OpenAPI (<c>/openapi.json</c>) declares the route with an empty
/// requestBody and an <c>object</c> response whose values are strings
/// (<c>additionalProperties: { "type": "string" }</c>). At wiring time the route
/// is a placeholder returning <c>501 {"detail":"T-BE-007 not yet implemented"}</c>.
/// This client therefore:
///   * posts a forward-compatible JSON envelope (base64 audio + metadata),
///   * binds any <c>{ "text": ..., "language": ... }</c> 200 body into
///     <see cref="TranscriptionResult"/> with outcome <c>Transcribed</c>,
///   * maps the upstream's <c>501 Not Implemented</c> placeholder onto outcome
///     <c>QueuedForRetry</c> (degraded-but-not-error: the gateway accepted the
///     audio, the upstream simply cannot transcribe yet), and
///   * lets transport faults / 5xx surface through the resilience pipeline
///     (retry → circuit breaker) attached in
///     <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>; a final
///     transport failure throws <see cref="WhisperUnavailableException"/>.
/// The wire field names are snake_case to match the FastAPI convention used
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
        var payload = new TranscribeUpstreamRequest(
            AudioBase64: Convert.ToBase64String(audio.Content),
            FileName: audio.FileName,
            ContentType: audio.ContentType,
            Language: language);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("v1/transcribe", payload, Json, ct);
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

        // The upstream placeholder: T-BE-007 not yet implemented. Treat as a
        // degraded outcome (audio accepted, transcription deferred) rather than
        // a hard failure — this keeps the mobile contract stable until the
        // upstream lands real transcription.
        if (response.StatusCode == HttpStatusCode.NotImplemented)
        {
            _log.LogInformation(
                "voice-transcription-service returned 501 (not yet implemented); deferring transcription");
            return new TranscriptionResult(
                AudioId: Guid.NewGuid().ToString("n"),
                Outcome: TranscriptionOutcome.QueuedForRetry,
                Transcription: null,
                Reason: "upstream_not_implemented");
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Let the controller-level decision treat this as unavailable; the
            // resilience pipeline already retried transient statuses.
            throw new WhisperUnavailableException(
                $"voice-transcription returned transient status {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<TranscribeUpstreamResponse>(Json, ct);
        var text = body?.Text ?? string.Empty;
        return new TranscriptionResult(
            AudioId: Guid.NewGuid().ToString("n"),
            Outcome: TranscriptionOutcome.Transcribed,
            Transcription: new WhisperTranscription(text, body?.Language ?? language),
            Reason: null);
    }

    /// <summary>
    /// Voice-on-create (JEB-1431). Forwards the audio to the upstream's canonical
    /// multipart route <c>POST /v1/voice/transcribe</c> (the route that returns the
    /// real <c>{transcript, confidence, language, duration_ms}</c> contract, backed
    /// by the owning service's real Whisper or its config-gated mock seam). The
    /// gateway requestId is mapped to the generic <c>Idempotency-Key</c> header so a
    /// retried draft replays the cached transcript. Size (413) / format (415) gates
    /// are validated at the gateway BEFORE this call (so no Whisper runs on reject);
    /// this method also surfaces any upstream 413/415 onto a typed exception.
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/voice/transcribe")
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

    private sealed record TranscribeUpstreamRequest(
        [property: JsonPropertyName("audio_base64")] string AudioBase64,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("content_type")] string ContentType,
        [property: JsonPropertyName("language")] string Language);

    private sealed record TranscribeUpstreamResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);

    // Canonical /v1/voice/transcribe response (JEB-43): {transcript, confidence, language, duration_ms}.
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
