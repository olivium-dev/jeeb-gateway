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

    private sealed record TranscribeUpstreamRequest(
        [property: JsonPropertyName("audio_base64")] string AudioBase64,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("content_type")] string ContentType,
        [property: JsonPropertyName("language")] string Language);

    private sealed record TranscribeUpstreamResponse(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language);
}
