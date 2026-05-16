using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Whisper;

/// <summary>
/// Implements T-backend-036 resilience policy:
///   1. Per-attempt 10s timeout via linked CTS.
///   2. Retry with exponential backoff up to <see cref="WhisperOptions.MaxAttempts"/>.
///   3. Circuit breaker trips after 5 consecutive failures (<see cref="IWhisperCircuitBreaker"/>).
///   4. On exhausted retries OR open breaker: save audio + enqueue for async retry.
/// </summary>
public sealed class ResilientTranscriptionService : ITranscriptionService
{
    private readonly IWhisperClient _whisper;
    private readonly IWhisperCircuitBreaker _breaker;
    private readonly IAudioStore _store;
    private readonly ITranscriptionFallbackQueue _queue;
    private readonly WhisperOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ResilientTranscriptionService> _log;

    public ResilientTranscriptionService(
        IWhisperClient whisper,
        IWhisperCircuitBreaker breaker,
        IAudioStore store,
        ITranscriptionFallbackQueue queue,
        IOptions<WhisperOptions> options,
        ILogger<ResilientTranscriptionService> log,
        TimeProvider? time = null)
    {
        _whisper = whisper;
        _breaker = breaker;
        _store = store;
        _queue = queue;
        _options = options.Value;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TranscriptionResult> TranscribeAsync(WhisperAudio audio, CancellationToken ct)
    {
        if (!_breaker.AllowRequest())
        {
            _log.LogWarning("whisper circuit open; falling back to audio-only mode");
            return await FallbackAsync(audio, "circuit_open", ct);
        }

        var attempts = Math.Max(1, _options.MaxAttempts);
        WhisperUnavailableException? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(_options.Timeout);

            try
            {
                var result = await _whisper.TranscribeAsync(audio, attemptCts.Token);
                _breaker.RecordSuccess();
                return new TranscriptionResult(
                    AudioId: Guid.NewGuid().ToString("n"),
                    Outcome: TranscriptionOutcome.Transcribed,
                    Transcription: result,
                    Reason: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancellation — surface immediately
            }
            catch (OperationCanceledException ex)
            {
                lastError = new WhisperUnavailableException("attempt timed out", ex);
                _breaker.RecordFailure();
                _log.LogWarning(ex, "whisper attempt {Attempt}/{Max} timed out", attempt, attempts);
            }
            catch (WhisperUnavailableException ex)
            {
                lastError = ex;
                _breaker.RecordFailure();
                _log.LogWarning(ex, "whisper attempt {Attempt}/{Max} failed: {Reason}",
                    attempt, attempts, ex.Message);
            }

            if (!_breaker.AllowRequest())
            {
                _log.LogWarning("whisper breaker tripped after attempt {Attempt}; stopping retries", attempt);
                break;
            }

            if (attempt < attempts)
            {
                await Task.Delay(BackoffFor(attempt), _time, ct);
            }
        }

        return await FallbackAsync(audio, lastError?.Message ?? "exhausted_retries", ct);
    }

    private TimeSpan BackoffFor(int attempt)
    {
        // attempt is 1-based; first backoff = InitialBackoff, then doubles up to MaxBackoff.
        var multiplier = Math.Pow(2, attempt - 1);
        var raw = TimeSpan.FromMilliseconds(_options.InitialBackoff.TotalMilliseconds * multiplier);
        return raw > _options.MaxBackoff ? _options.MaxBackoff : raw;
    }

    private async Task<TranscriptionResult> FallbackAsync(WhisperAudio audio, string reason, CancellationToken ct)
    {
        var audioId = await _store.SaveAsync(audio, ct);
        await _queue.EnqueueAsync(new QueuedTranscription(audioId, reason, _time.GetUtcNow()), ct);
        return new TranscriptionResult(
            AudioId: audioId,
            Outcome: TranscriptionOutcome.QueuedForRetry,
            Transcription: null,
            Reason: reason);
    }
}
