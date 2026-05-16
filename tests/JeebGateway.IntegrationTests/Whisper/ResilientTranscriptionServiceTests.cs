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
    public void Backoff_Follows_1s_2s_4s_Pattern_With_Default_Options()
    {
        var (service, _) = NewService(
            new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom"))),
            opts =>
            {
                opts.InitialBackoff = TimeSpan.FromSeconds(1);
                opts.MaxBackoff = TimeSpan.FromSeconds(8);
            });

        service.BackoffFor(1).Should().Be(TimeSpan.FromSeconds(1));
        service.BackoffFor(2).Should().Be(TimeSpan.FromSeconds(2));
        service.BackoffFor(3).Should().Be(TimeSpan.FromSeconds(4));
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

        await service.TranscribeAsync(SampleAudio, CancellationToken.None);
        deps.Breaker.State.Should().Be(CircuitState.Closed);

        await service.TranscribeAsync(SampleAudio, CancellationToken.None);
        deps.Breaker.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task Fallback_Provider_Is_Invoked_When_Primary_Exhausts_Retries()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var fallback = new FakeFallbackProvider(
            available: true,
            result: new WhisperTranscription("fallback text", "ar"));

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.InitialBackoff = TimeSpan.Zero;
        }, fallback);

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.Transcribed);
        result.Transcription!.Text.Should().Be("fallback text");
        fallback.CallCount.Should().Be(1);
        deps.Queue.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Fallback_Provider_Is_Invoked_When_Circuit_Open()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var fallback = new FakeFallbackProvider(
            available: true,
            result: new WhisperTranscription("secondary", "ar"));

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.InitialBackoff = TimeSpan.Zero;
            opts.CircuitBreakerFailureThreshold = 5;
        }, fallback);

        for (var i = 0; i < 5; i++) deps.Breaker.RecordFailure();

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.Transcribed);
        result.Transcription!.Text.Should().Be("secondary");
    }

    [Fact]
    public async Task Queues_When_Fallback_Provider_Fails()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var fallback = new FakeFallbackProvider(available: true, throwOnCall: true);

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.InitialBackoff = TimeSpan.Zero;
        }, fallback);

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        deps.Queue.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task Queues_When_Fallback_Provider_Returns_Null()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var fallback = new FakeFallbackProvider(available: true, result: null);

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.InitialBackoff = TimeSpan.Zero;
        }, fallback);

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        deps.Queue.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task Queues_When_Fallback_Provider_Not_Available()
    {
        var whisper = new FakeWhisper(_ => Throw(new WhisperUnavailableException("boom")));
        var fallback = new FakeFallbackProvider(available: false);

        var (service, deps) = NewService(whisper, opts =>
        {
            opts.MaxAttempts = 1;
            opts.InitialBackoff = TimeSpan.Zero;
        }, fallback);

        var result = await service.TranscribeAsync(SampleAudio, CancellationToken.None);

        result.Outcome.Should().Be(TranscriptionOutcome.QueuedForRetry);
        fallback.CallCount.Should().Be(0);
        deps.Queue.Snapshot().Should().HaveCount(1);
    }

    private sealed record Deps(
        WhisperCircuitBreaker Breaker,
        InMemoryAudioStore Store,
        InMemoryTranscriptionFallbackQueue Queue,
        FakeTimeProvider Time);

    private static (ResilientTranscriptionService Service, Deps Deps) NewService(
        IWhisperClient whisper,
        Action<WhisperOptions>? configure = null,
        IFallbackTranscriptionProvider? fallbackProvider = null)
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
            whisper, breaker,
            fallbackProvider ?? new NoOpFallbackTranscriptionProvider(),
            store, queue, opts,
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

    private sealed class FakeFallbackProvider : IFallbackTranscriptionProvider
    {
        private readonly WhisperTranscription? _result;
        private readonly bool _throwOnCall;

        public FakeFallbackProvider(bool available, WhisperTranscription? result = null, bool throwOnCall = false)
        {
            IsAvailable = available;
            _result = result;
            _throwOnCall = throwOnCall;
        }

        public bool IsAvailable { get; }
        public int CallCount { get; private set; }

        public Task<WhisperTranscription?> TranscribeAsync(WhisperAudio audio, CancellationToken ct)
        {
            CallCount++;
            if (_throwOnCall)
                throw new InvalidOperationException("fallback provider exploded");
            return Task.FromResult(_result);
        }
    }
}
