using JeebGateway.Whisper;
using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over voice-transcription-service (FastAPI, host port 10062,
/// liveness probe <c>/healthz</c>, readiness <c>/readyz</c>).
///
/// This is the sole runtime transcription boundary. The owner accepts multipart
/// audio at <c>POST /v1/transcribe</c>, may return a completed 200 or a durable
/// queued 202, and exposes the queued job at <c>/v1/transcriptions/{audioId}</c>.
/// The gateway has no local Whisper, audio store, circuit, retry queue, or DLQ.
/// </summary>
public interface IVoiceTranscriptionClient
{
    /// <summary>
    /// Submits audio to voice-transcription-service for transcription.
    /// Returns the upstream outcome mapped onto the gateway's
    /// <see cref="TranscriptionResult"/> contract so the controller response
    /// shape is identical regardless of which path served the request.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(WhisperAudio audio, string language, CancellationToken ct);

    /// <summary>
    /// Voice-on-create overload (JEB-1431/JEB-67). Forwards the multipart audio to
    /// the upstream's canonical <c>POST /v1/transcribe</c> (the single stable route
    /// pinned by JEB-1483; the legacy <c>/v1/voice/transcribe</c> alias is deprecated
    /// upstream per JEB-1482) with the gateway's <paramref name="idempotencyKey"/>
    /// (the client requestId) mapped onto the generic <c>Idempotency-Key</c> header
    /// so a network retry of the same draft returns the cached transcript. The
    /// transcript VALUE is produced entirely by the owning service (real Whisper or
    /// its config-gated mock seam) — the gateway holds no STT logic. Returns
    /// transcript + confidence + resolved language.
    /// </summary>
    Task<TranscriptionResult> TranscribeVoiceAsync(
        WhisperAudio audio, string language, string? idempotencyKey, CancellationToken ct);

    /// <summary>Returns the owner service readiness document.</summary>
    Task<JsonElement> GetReadinessAsync(CancellationToken ct) =>
        throw new NotSupportedException("Voice readiness is not implemented by this test adapter.");

    /// <summary>Returns durable owner state for one queued transcription.</summary>
    Task<JsonElement> GetTranscriptionStatusAsync(string audioId, CancellationToken ct) =>
        throw new NotSupportedException("Voice status is not implemented by this test adapter.");
}
