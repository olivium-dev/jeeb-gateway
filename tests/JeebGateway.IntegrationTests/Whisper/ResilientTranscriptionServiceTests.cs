using FluentAssertions;
using JeebGateway.Whisper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Whisper;

public class ResilientTranscriptionServiceTests
{
    private static readonly WhisperAudio SampleAudio =
        new(new byte[] { 1, 2, 3 }, "clip.m4a", "audio/m4a");

    [Fact]
    public async Task Returns_Transcription_On_First_Success()
    {
        var (service, deps) = NewService(new FakeWhisper(_ => Result(new WhisperTranscription("hello", "ar"))));

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.Transcribed);
        result.Transcription!.Text.Should().Be("hello");
        deps.Queue.Snapshot().Should().BeEmpty();
        deps.Breaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Retries_On_Transient_Failure_Then_Succeeds()
    {
        var calls = 0;
        var whisper = new FakeWhisper(_ =>
        {
            calls++;
            return calls < 2
                ? Throw(new WhisperUnavailableException("boom"))
                : Result(new WhisperTranscription("ok", "ar"));
        });

        var (service, deps) = NewService(whisper);

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.Transcribed);
        calls.Should().Be(2);
        deps.Queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Backoff_Doubles_Between_Attempts()
    {
        var calls = 0;
        var whisper = new FakeWhisper(_ =>
        {
            calls++;
            return Throw(new WhisperUnavailableException("boom"));
        });

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 3;
            opts.InitialBackoff = TimeSpan.FromMilliseconds(100);
            opts.MaxBackoff = TimeSpan.FromSeconds(1);
        });

        var task = service.TranscribeAsync(SampleAudio, CancellationToken.None);

        // The fake delays cooperatively via TimeProvider; advance time to release each backoff window.
        await Task.Yield();
        deps.Time.Advance(TimeSpan.FromMilliseconds(100)); // after attempt 1
        await Task.Yield();
        deps.Time.Advance(TimeSpan.FromMilliseconds(200)); // after attempt 2
        await Task.Yield();

        var result = await task;

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Exhausts_Retries_Then_Falls_Back_To_Queue()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 3;
            opts.InitialBackoff = TimeSpan.Zero;
            opts.MaxBackoff = TimeSpan.Zero;
        });

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        result.Reason.Should().NotBeNullOrEmpty();
        deps.Queue.Snapshot().Should().HaveCount(1);
        deps.Store.Count.Should().Be(1);
    }

    [Fact]
    public async Task Timeout_Counts_As_Failure_And_Falls_Back()
    {
        var whisper = new FakeWhisper(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new WhisperTranscription("never", "ar");
        });

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.Timeout = TimeSpan.FromMilliseconds(50);
            opts.InitialBackoff = TimeSpan.Zero;
        });

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        deps.Queue.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task Open_Breaker_Short_Circuits_To_Fallback_Without_Calling_Whisper()
    {
        var calls = 0;
        var whisper = new FakeWhisper(_ =>
        {
            calls++;
            return Throw(new WhisperUnavailableException("boom"));
        });

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.InitialBackoff = TimeSpan.Zero;
            opts.CircuitBreakerFailureThreshold = 5;
        });

        // Trip the breaker by recording 5 consecutive failures directly.
        for (var i = 0; i < 5; i++) deps.Breaker.RecordFailure();
        deps.Breaker.State.Should().Be(CircuitState.Open);

        calls = 0;
        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        result.Reason.Should().Be("circuit_open");
        calls.Should().Be(0);
        deps.Queue.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task Breaker_Opens_After_Five_Consecutive_Failures_Across_Attempts()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 3;
            opts.InitialBackoff = TimeSpan.Zero;
            opts.MaxBackoff = TimeSpan.Zero;
            opts.CircuitBreakerFailureThreshold = 5;
        });

        // First call: 3 failures.
        await service.TranscribeAsync(SampleAudio, CancellationToken.None);
        deps.Breaker.State.Should().Be(CircuitState.Closed);

        // Second call: 2 more failures = 5 total -> open.
        await service.TranscribeAsync(SampleAudio, CancellationToken.None);
        deps.Breaker.State.Should().Be(CircuitState.Open);
    }

    private sealed record Deps(
        WhisperCircuitBreaker Breaker,
        InMemoryAudioStore Store,
        InMemoryTranscriptionFallbackQueue Queue,
        FakeTimeProvider Time);

    private static (ResilientTranscriptionService Service, Deps Deps) NewService(
        IWhisperClient whisper,
        Action<WhisperOptions>? configure = null)
    {
        var options = new WhisperOptions
        {
            MaxAttempts = 3,
            Timeout = TimeSpan.FromSeconds(10),
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            MaxBackoff = TimeSpan.FromMilliseconds(8),
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30)
        };
        configure?.Invoke(options);

        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var opts = Options.Create(options);
        var breaker = new WhisperCircuitBreaker(opts, time);
        var store = new InMemoryAudioStore();
        var queue = new InMemoryTranscriptionFallbackQueue();
        var service = new ResilientTranscriptionService(
            whisper, breaker, store, queue, opts,
            NullLogger<ResilientTranscriptionService>.Instance,
            time);
        return (service, new Deps(breaker, store, queue, time));
    }

    private static Task<WhisperTranscription> Result(WhisperTranscription t) => Task.FromResult(t);
    private static Task<WhisperTranscription> Throw(Exception ex) => Task.FromException<WhisperTranscription>(ex);

    private sealed class FakeWhisper : IWhisperClient
    {
        private readonly Func<CancellationToken, Task<WhisperTranscription>> _impl;
        public FakeWhisper(Func<CancellationToken, Task<WhisperTranscription>> impl) => _impl = impl;
        public Task<WhisperTranscription> TranscribeAsync(WhisperAudio audio, CancellationToken ct) => _impl(ct);
    }
}
