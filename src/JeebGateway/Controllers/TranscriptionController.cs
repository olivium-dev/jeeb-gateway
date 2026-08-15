using JeebGateway.Auth.Capabilities;
using JeebGateway.Services.Clients;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("transcribe")]
public class TranscriptionController : ControllerBase
{
    private readonly IVoiceTranscriptionClient _upstream;

    public TranscriptionController(IVoiceTranscriptionClient upstream)
    {
        _upstream = upstream;
    }

    [HttpPost]
    // ADR-005 L2 §H–J participant {client, jeeber}: transcription request. The gateway FallbackPolicy
    // (ADR-004) already requires an identified caller; this declares the participant user-type.
    [RequireCapability(Capabilities.TranscriptionRequest)]
    [ProducesResponseType(typeof(TranscribeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TranscribeResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Post([FromBody] TranscribeRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.AudioBase64))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "audio is required",
                Status = StatusCodes.Status400BadRequest
            });
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body.AudioBase64);
        }
        catch (FormatException)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "audioBase64 must be valid base64",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var audio = new WhisperAudio(bytes, body.FileName, body.ContentType);

        TranscriptionResult result;
        try
        {
            result = await _upstream.TranscribeAsync(audio, "ar", ct);
        }
        catch (VoiceAudioRejectedException rejected)
        {
            return StatusCode(rejected.StatusCode, new ProblemDetails
            {
                Title = rejected.Reason,
                Status = rejected.StatusCode,
                Type = $"https://jeeb.dev/errors/{rejected.Reason.Replace('_', '-')}",
            });
        }
        catch (WhisperUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Voice transcription owner is unavailable.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/voice-unavailable",
            });
        }

        if (result.Outcome == TranscriptionOutcome.Transcribed)
        {
            return Ok(new TranscribeResponse(
                AudioId: result.AudioId,
                Status: "transcribed",
                Transcription: result.Transcription!.Text,
                Language: result.Transcription.Language,
                Reason: null));
        }

        return Accepted(new TranscribeResponse(
            AudioId: result.AudioId,
            Status: "queued",
            Transcription: null,
            Language: null,
            Reason: result.Reason));
    }

    /// <summary>Lightweight status probe for the Whisper transcription subsystem.</summary>
    [HttpGet("status")]
    // ADR-005 L2 §A public: Whisper subsystem status/health probe (circuit-breaker state) — no user-type gate.
    [PublicEndpoint("Whisper subsystem status probe — ADR-005 §A public (health/status family).")]
    [ProducesResponseType(typeof(WhisperStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        try
        {
            var readiness = await _upstream.GetReadinessAsync(ct);
            var healthy = readiness.TryGetProperty("status", out var status)
                          && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);

            return Ok(new WhisperStatusResponse(
                CircuitState: "OwnedByVoiceTranscriptionService",
                FallbackAvailable: false,
                PendingQueueDepth: -1,
                Healthy: healthy));
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Voice transcription owner is unavailable.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/voice-unavailable",
            });
        }
    }

    /// <summary>Proxy the durable status of one owner-managed transcription.</summary>
    [HttpGet("status/{audioId}")]
    [PublicEndpoint("Owner-managed transcription status probe.")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTranscriptionStatus(
        string audioId,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _upstream.GetTranscriptionStatusAsync(audioId, ct));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound();
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Voice transcription owner is unavailable.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/voice-unavailable",
            });
        }
    }
}
