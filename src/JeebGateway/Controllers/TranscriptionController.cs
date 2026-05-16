using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("transcribe")]
public class TranscriptionController : ControllerBase
{
    private readonly ITranscriptionService _service;

    public TranscriptionController(ITranscriptionService service)
    {
        _service = service;
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
}
