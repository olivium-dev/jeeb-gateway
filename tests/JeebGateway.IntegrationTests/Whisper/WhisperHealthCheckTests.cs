using FluentAssertions;
using JeebGateway.Whisper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Whisper;

public class WhisperHealthCheckTests
{
    [Fact]
    public async Task Healthy_When_Circuit_Closed()
    {
        var (check, _, _, _) = NewCheck();

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["circuitState"].Should().Be("Closed");
    }

    [Fact]
    public async Task Degraded_When_Circuit_HalfOpen()
    {
        var (check, breaker, _, time) = NewCheck();

        for (var i = 0; i < 5; i++) breaker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(31));
        breaker.State.Should().Be(CircuitState.HalfOpen);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["circuitState"].Should().Be("HalfOpen");
    }

    [Fact]
    public async Task Unhealthy_When_Circuit_Open_Without_Fallback()
    {
        var (check, breaker, _, _) = NewCheck(fallbackAvailable: false);

        for (var i = 0; i < 5; i++) breaker.RecordFailure();

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["fallbackAvailable"].Should().Be(false);
    }

    [Fact]
    public async Task Degraded_When_Circuit_Open_With_Fallback()
    {
        var (check, breaker, _, _) = NewCheck(fallbackAvailable: true);

        for (var i = 0; i < 5; i++) breaker.RecordFailure();

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["fallbackAvailable"].Should().Be(true);
    }

    [Fact]
    public async Task Reports_Queue_Depth()
    {
        var (check, _, queue, _) = NewCheck();

        await queue.EnqueueAsync(new QueuedTranscription("a1", "test", DateTimeOffset.UtcNow), CancellationToken.None);
        await queue.EnqueueAsync(new QueuedTranscription("a2", "test", DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Data["pendingQueueDepth"].Should().Be(2);
    }

    private static (WhisperHealthCheck Check, WhisperCircuitBreaker Breaker, InMemoryTranscriptionFallbackQueue Queue, FakeTimeProvider Time) NewCheck(
        bool fallbackAvailable = false)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var opts = Options.Create(new WhisperOptions
        {
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30)
        });
        var breaker = new WhisperCircuitBreaker(opts, time);
        var queue = new InMemoryTranscriptionFallbackQueue();
        var fallback = new ConfigurableFallback(fallbackAvailable);
        var check = new WhisperHealthCheck(breaker, fallback, queue);
        return (check, breaker, queue, time);
    }

    private sealed class ConfigurableFallback : IFallbackTranscriptionProvider
    {
        public ConfigurableFallback(bool available) => IsAvailable = available;
        public bool IsAvailable { get; }
        public Task<WhisperTranscription?> TranscribeAsync(WhisperAudio audio, CancellationToken ct)
            => Task.FromResult<WhisperTranscription?>(null);
    }
}
