using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Voice-note ordering BFF slice (S04 / JEB-1431 / JEB-67 / JEB-43).
///
/// THIN by construction: this controller holds NO speech-to-text logic. It only
/// (1) parses the multipart voice upload, (2) enforces the gateway preconditions
/// that must reject BEFORE any Whisper runs (auth/role, audio size, audio format,
/// BR-9 active-request cap), (3) maps the client <c>requestId</c> onto the generic
/// <c>Idempotency-Key</c> and forces <c>language=ar</c>, then (4) delegates the
/// transcription to the OWNING voice-transcription-service via the typed
/// <see cref="IVoiceTranscriptionClient"/>. The transcript VALUE comes entirely from
/// that service (real Whisper or its config-gated mock seam) — the gateway echoes it
/// and seeds the draft request description with it.
///
/// Route is <c>POST /v1/requests</c> (the S04 voice surface). The typed-text JSON
/// create stays on the existing <c>POST /requests</c> (RequestsController) untouched.
/// Gated behind <c>FeatureFlags:UseUpstream:Voice</c>: when OFF the route returns 503
/// (net-new path, no legacy fallback), so flipping the flag is the single switch.
/// </summary>
[ApiController]
[Route("v1/requests")]
public sealed class RequestVoiceController : ControllerBase
{
    /// <summary>S04 BR-S04 / JEB-43 AC3: 5 MB hard cap on voice audio.</summary>
    public const long MaxAudioBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Accepted upload content-types (S04 N3 whitelist: opus, mp3, m4a, wav).
    /// aac and everything else => 415 before any Whisper.
    /// </summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/opus", "audio/ogg", "audio/mpeg", "audio/mp3",
        "audio/m4a", "audio/mp4", "audio/x-m4a", "audio/wav", "audio/x-wav", "audio/wave",
    };

    private readonly IRequestsStore _store;
    private readonly ITiersStore _tiers;
    private readonly IVoiceTranscriptionClient _voice;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public RequestVoiceController(
        IRequestsStore store,
        ITiersStore tiers,
        IVoiceTranscriptionClient voice,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _store = store;
        _tiers = tiers;
        _voice = voice;
        _flags = flags;
    }

    /// <summary>
    /// Submit a voice-note order. Multipart fields: <c>audio</c> (file),
    /// <c>requestId</c> (text, idempotency anchor), <c>tier</c> (text).
    /// </summary>
    [HttpPost]
    [RequireRole(Roles.Client)]
    [RequireActiveUser]
    [RequestSizeLimit(8 * 1024 * 1024)]
    [ProducesResponseType(typeof(VoiceRequestResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SubmitVoice(
        [FromForm] VoiceOrderForm form,
        CancellationToken ct)
    {
        var audio = form.Audio;
        var requestId = form.RequestId;
        var tier = form.Tier;

        // Net-new path kill switch: when the upstream voice route is not enabled in
        // this environment, return 503 rather than dialing an unconfigured host.
        if (!_flags.CurrentValue.Voice)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Voice transcription is not enabled in this environment.",
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/voice-disabled"
            });
        }

        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        if (audio is null || audio.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "audio file part is required.",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/audio-required"
            });
        }

        // (N1) 413 — oversize audio is rejected BEFORE any Whisper call.
        if (audio.Length > MaxAudioBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new ProblemDetails
            {
                Title = "Audio payload exceeds the 5 MB limit.",
                Detail = $"audio is {audio.Length} bytes; limit is {MaxAudioBytes}",
                Status = StatusCodes.Status413PayloadTooLarge,
                Type = "https://jeeb.dev/errors/audio-too-large"
            });
        }

        // (N3) 415 — unsupported format is rejected BEFORE any Whisper call.
        var contentType = (audio.ContentType ?? string.Empty).Split(';', 2)[0].Trim();
        if (!AllowedContentTypes.Contains(contentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new ProblemDetails
            {
                Title = "Unsupported audio format.",
                Detail = $"content-type '{contentType}' is not in the whitelist (opus, mp3, m4a, wav).",
                Status = StatusCodes.Status415UnsupportedMediaType,
                Type = "https://jeeb.dev/errors/unsupported-format"
            });
        }

        // (A1) Idempotent re-submit: if a row already exists under this requestId
        // anchor and belongs to the caller, return it unchanged (no duplicate, no
        // second Whisper call) — the network-retry / double-tap case.
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            var prior = await _store.GetAsync(requestId, ct);
            if (prior is not null && string.Equals(prior.ClientId, clientId, StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status200OK, new VoiceRequestResponse
                {
                    RequestId = prior.Id,
                    Id = prior.Id,
                    Status = prior.Status,
                    Transcription = prior.Transcription,
                    TranscriptionConfidence = prior.TranscriptionConfidence,
                    Language = "ar",
                    TierId = prior.TierId
                });
            }
        }

        var tierId = string.IsNullOrWhiteSpace(tier) ? "standard" : tier.Trim();
        if (!await _tiers.ExistsAsync(tierId, ct))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "tier does not match any active delivery tier.",
                Detail = $"tier={tierId}",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://jeeb.dev/errors/tier-not-found"
            });
        }

        byte[] bytes;
        await using (var stream = audio.OpenReadStream())
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        // S04 BR-S04-7/8: the gateway FORCES language=ar and maps the client
        // requestId onto the generic Idempotency-Key the upstream dedups on. The
        // transcript VALUE is produced by the owning service (real or gated mock).
        var whisperAudio = new WhisperAudio(bytes, audio.FileName ?? "audio.bin", contentType);
        TranscriptionResult result;
        try
        {
            result = await _voice.TranscribeVoiceAsync(whisperAudio, "ar", requestId, ct);
        }
        catch (VoiceAudioRejectedException rejected)
        {
            // Upstream's own size/format gate (defence in depth) — surface the same
            // status; still no request row created.
            return StatusCode(rejected.StatusCode, new ProblemDetails
            {
                Title = rejected.Reason,
                Status = rejected.StatusCode,
                Type = $"https://jeeb.dev/errors/{rejected.Reason.Replace('_', '-')}"
            });
        }
        catch (WhisperUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Voice transcription is temporarily unavailable.",
                Detail = ex.Message,
                Status = StatusCodes.Status503ServiceUnavailable,
                Type = "https://jeeb.dev/errors/voice-unavailable"
            });
        }

        var transcript = result.Transcription?.Text ?? string.Empty;
        var language = result.Transcription?.Language ?? "ar";
        var confidence = result.Transcription?.Confidence;

        // Seed the draft request with the transcript as the description (FR-3.4).
        var input = new CreateRequestInput
        {
            // Key the row by the requestId anchor so the voice read-back (H2) resolves
            // and re-submits collapse onto the same row (A1). Null => store mints a GUID.
            Id = string.IsNullOrWhiteSpace(requestId) ? null : requestId,
            ClientId = clientId,
            Description = string.IsNullOrWhiteSpace(transcript) ? "(voice order)" : transcript,
            Transcription = transcript,
            TranscriptionConfidence = confidence,
            AudioUrl = null,
            Photos = Array.Empty<string>(),
            TierId = tierId,
            // S04 treats geolocation as a downstream black box; the voice slice uses
            // the same valid Beirut default the scenario pins for the typed path so
            // the create satisfies the store's WGS84 precondition.
            PickupLocation = new GeoPoint { Lat = 33.88, Lng = 35.50 },
            DropoffLocation = new GeoPoint { Lat = 33.89, Lng = 35.51 },
            PickupAddress = null,
            DropoffAddress = null,
            ScheduledAt = null
        };

        DeliveryRequest created;
        try
        {
            created = await _store.TryCreateWithLimitAsync(input, RequestsController.ActiveRequestsLimit, ct);
        }
        catch (TooManyActiveRequestsException ex)
        {
            // (N9) BR-9 active-request cap — 409 before the draft is created.
            return Conflict(new ProblemDetails
            {
                Title = RequestsController.LimitExceededMessage,
                Detail = $"Client has {ex.ActiveCount} active requests (limit {ex.Limit}).",
                Status = StatusCodes.Status409Conflict,
                Type = "https://jeeb.dev/errors/too-many-active-requests"
            });
        }

        // Echo the S04 voice contract: {requestId, transcription, transcription_confidence, language}.
        // requestId echoes the client idempotency anchor when supplied, else the new row id.
        var echoedRequestId = string.IsNullOrWhiteSpace(requestId) ? created.Id : requestId;
        return Created($"/v1/requests/{echoedRequestId}", new VoiceRequestResponse
        {
            RequestId = echoedRequestId,
            Id = created.Id,
            Status = created.Status,
            Transcription = transcript,
            TranscriptionConfidence = confidence,
            Language = language,
            TierId = created.TierId
        });
    }

    /// <summary>
    /// Voice-create read-back (S04 H2). Aligns the <c>/v1/requests/{id}</c> read
    /// surface used by the voice flow onto the same owner-scoped store read as
    /// <c>GET /requests/{id}</c>, echoing the voice contract fields.
    /// </summary>
    [HttpGet("{requestId}")]
    [RequireRole(Roles.Client)]
    [RequireActiveUser]
    [ProducesResponseType(typeof(VoiceRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string requestId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var clientId, out var problem)) return problem;

        var existing = await _store.GetAsync(requestId, ct);
        if (existing is null) return NotFound();
        if (!string.Equals(existing.ClientId, clientId, StringComparison.Ordinal)) return NotFound();

        return Ok(new VoiceRequestResponse
        {
            RequestId = existing.Id,
            Id = existing.Id,
            Status = existing.Status,
            Transcription = existing.Transcription,
            TranscriptionConfidence = existing.TranscriptionConfidence,
            Language = "ar",
            TierId = existing.TierId
        });
    }
}

/// <summary>
/// Multipart form for the voice-note order (S04). A single [FromForm] model is the
/// Swashbuckle-supported way to combine an IFormFile with scalar form fields.
/// </summary>
public sealed class VoiceOrderForm
{
    /// <summary>The voice audio file (opus / mp3 / m4a / wav, max 5 MB).</summary>
    public IFormFile? Audio { get; set; }

    /// <summary>Client idempotency anchor; mapped onto the upstream Idempotency-Key.</summary>
    public string? RequestId { get; set; }

    /// <summary>Selected delivery tier code (defaults to "standard").</summary>
    public string? Tier { get; set; }
}

/// <summary>S04 voice-create response contract.</summary>
public sealed class VoiceRequestResponse
{
    // The S04 contract pins snake_case for the voice fields (matching the FastAPI
    // upstream convention). transcription_confidence needs the explicit snake_case
    // name so the documented contract field is present (default web serialisation
    // would emit transcriptionConfidence).
    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public required string RequestId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("transcription")]
    public string? Transcription { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("transcription_confidence")]
    public double? TranscriptionConfidence { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("language")]
    public required string Language { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("tierId")]
    public string? TierId { get; init; }
}
