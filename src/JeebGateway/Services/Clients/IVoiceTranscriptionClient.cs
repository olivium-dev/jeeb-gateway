using JeebGateway.Whisper;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over voice-transcription-service (FastAPI, host port 10062,
/// liveness probe <c>/healthz</c>, readiness <c>/readyz</c>).
///
/// thin-BFF fan-out (3 of 4). Consumed by
/// <see cref="JeebGateway.Controllers.TranscriptionController"/> when
/// <c>FeatureFlags:UseUpstream:Voice</c> is true; the in-process Whisper /
/// circuit-breaker / fallback path (<see cref="ITranscriptionService"/>) stays
/// the flag-OFF default and is NOT deleted in this PR.
///
/// REPOINT-VS-NEW: the existing <see cref="WhisperClient"/> targets OpenAI's
/// <c>POST audio/transcriptions</c> (multipart <c>file</c>/<c>model</c>/
/// <c>language</c>/<c>response_format</c> → <c>{ "text": ... }</c>). The upstream
/// here exposes a DIFFERENT route — <c>POST /v1/transcribe</c> — with an EMPTY
/// OpenAPI requestBody and an <c>additionalProperties: string</c> response, and
/// at the time of wiring returns <c>501 {"detail":"T-BE-007 not yet implemented"}</c>
/// for every payload shape probed (empty, JSON, multipart). The routes and
/// contracts differ materially, so this is a NET-NEW thin client rather than a
/// repoint of <see cref="WhisperClient"/>. It posts a forward-compatible JSON
/// envelope (base64 audio + filename/contentType/language) and tolerates the
/// upstream's not-yet-implemented placeholder so the seam is in place the moment
/// the upstream implements T-BE-007.
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
    /// the upstream's canonical <c>POST /v1/voice/transcribe</c> with the gateway's
    /// <paramref name="idempotencyKey"/> (the client requestId) mapped onto the
    /// generic <c>Idempotency-Key</c> header so a network retry of the same draft
    /// returns the cached transcript. The transcript VALUE is produced entirely by
    /// the owning service (real Whisper or its config-gated mock seam) — the gateway
    /// holds no STT logic. Returns transcript + confidence + resolved language.
    /// </summary>
    Task<TranscriptionResult> TranscribeVoiceAsync(
        WhisperAudio audio, string language, string? idempotencyKey, CancellationToken ct);
}
