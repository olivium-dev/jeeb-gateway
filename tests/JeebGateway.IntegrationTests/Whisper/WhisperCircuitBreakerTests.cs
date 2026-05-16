using FluentAssertions;
using JeebGateway.Whisper;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Whisper;

public class WhisperCircuitBreakerTests
{
    [Fact]
    public void Closed_When_No_Failures()
    {
        var breaker = NewBreaker(out _);
        breaker.State.Should().Be(CircuitState.Closed);
        breaker.AllowRequest().Should().BeTrue();
    }

    [Fact]
    public void Opens_After_Threshold_Consecutive_Failures()
    {
        var breaker = NewBreaker(out _);
        for (var i = 0; i < 5; i++) breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Open);
        breaker.AllowRequest().Should().BeFalse();
    }

    [Fact]
    public void Success_Resets_Counter_Before_Tripping()
    {
        var breaker = NewBreaker(out _);
        for (var i = 0; i < 4; i++) breaker.RecordFailure();
        breaker.RecordSuccess();
        for (var i = 0; i < 4; i++) breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void Transitions_To_HalfOpen_After_OpenDuration()
    {
        var breaker = NewBreaker(out var time);
        for (var i = 0; i < 5; i++) breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Open);

        time.Advance(TimeSpan.FromSeconds(31));

        breaker.State.Should().Be(CircuitState.HalfOpen);
        breaker.AllowRequest().Should().BeTrue();
    }

    private static WhisperCircuitBreaker NewBreaker(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var opts = Options.Create(new WhisperOptions
        {
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30)
        });
        return new WhisperCircuitBreaker(opts, time);
    }
}
