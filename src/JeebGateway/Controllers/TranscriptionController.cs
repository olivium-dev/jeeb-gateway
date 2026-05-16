using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("transcribe")]
public class TranscriptionController : ControllerBase
{
    private readonly ITranscriptionService _service;
    private readonly IWhisperCircuitBreaker _breaker;
    private readonly IFallbackTranscriptionProvider _fallbackProvider;
    private readonly ITranscriptionFallbackQueue _queue;

    public TranscriptionController(
        ITranscriptionService service,
        IWhisperCircuitBreaker breaker,
        IFallbackTranscriptionProvider fallbackProvider,
        ITranscriptionFallbackQueue queue)
    {
        _service = service;
        _breaker = breaker;
        _fallbackProvider = fallbackProvider;
        _queue = queue;
    }

    [HttpPost]
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
        var result = await _service.TranscribeAsync(audio, ct);

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
    [ProducesResponseType(typeof(WhisperStatusResponse), StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var state = _breaker.State;
        var queueDepth = _queue.Snapshot().Count;

        return Ok(new WhisperStatusResponse(
            CircuitState: state.ToString(),
            FallbackAvailable: _fallbackProvider.IsAvailable,
            PendingQueueDepth: queueDepth,
            Healthy: state != CircuitState.Open));
    }
}
