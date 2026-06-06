using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
    private readonly IVoiceTranscriptionClient _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IOptionsMonitor<WhisperOptions> _whisperOptions;

    public TranscriptionController(
        ITranscriptionService service,
        IWhisperCircuitBreaker breaker,
        IFallbackTranscriptionProvider fallbackProvider,
        ITranscriptionFallbackQueue queue,
        IVoiceTranscriptionClient upstream,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IOptionsMonitor<WhisperOptions> whisperOptions)
    {
        _service = service;
        _breaker = breaker;
        _fallbackProvider = fallbackProvider;
        _queue = queue;
        _upstream = upstream;
        _flags = flags;
        _whisperOptions = whisperOptions;
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

        // thin-BFF fan-out (3 of 4): when FeatureFlags:UseUpstream:Voice is ON,
        // proxy voice-transcription-service via the typed client. When OFF (the
        // default), use the in-process resilient Whisper path
        // (ITranscriptionService) — circuit breaker, retries, fallback queue —
        // which is preserved and NOT deleted in this PR. A transient upstream
        // failure (WhisperUnavailableException after the resilience pipeline is
        // exhausted) degrades to the same "queued" 202 shape rather than a 500,
        // so the mobile contract is identical on both paths.
        TranscriptionResult result;
        if (_flags.CurrentValue.Voice)
        {
            try
            {
                result = await _upstream.TranscribeAsync(
                    audio, _whisperOptions.CurrentValue.Language, ct);
            }
            catch (WhisperUnavailableException)
            {
                result = new TranscriptionResult(
                    AudioId: Guid.NewGuid().ToString("n"),
                    Outcome: TranscriptionOutcome.QueuedForRetry,
                    Transcription: null,
                    Reason: "upstream_unavailable");
            }
        }
        else
        {
            result = await _service.TranscribeAsync(audio, ct);
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
