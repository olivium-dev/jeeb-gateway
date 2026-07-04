using System.Diagnostics;
using FluentAssertions;
using JeebGateway.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

/// <summary>
/// GW12-OBS-4 — the global <see cref="UpstreamExceptionHandler"/> incident log line must
/// carry the correlation id (the value a client/support ticket quotes) and the OTel trace
/// id (the key that stitches the line to its distributed trace), so an on-call engineer
/// landing on THIS line has both grep keys inline.
/// </summary>
public class UpstreamExceptionHandlerCorrelationTests
{
    [Fact]
    public async Task Logs_CorrelationId_From_Items_On_The_Incident_Line()
    {
        var logger = new RecordingLogger<UpstreamExceptionHandler>();
        var handler = new UpstreamExceptionHandler(new FakeProblemDetailsService(), logger);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/saved-locations";
        ctx.Items["CorrelationId"] = "corr-incident-9";

        var handled = await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        handled.Should().BeTrue();
        logger.LastMessage.Should().NotBeNull();
        logger.LastMessage.Should().Contain("corr-incident-9");
        logger.LastLevel.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task Logs_TraceId_When_An_Activity_Is_Current()
    {
        var logger = new RecordingLogger<UpstreamExceptionHandler>();
        var handler = new UpstreamExceptionHandler(new FakeProblemDetailsService(), logger);

        using var activity = new Activity("test-op").Start();
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/ping";

        await handler.TryHandleAsync(ctx, new HttpRequestException("upstream down"), CancellationToken.None);

        logger.LastMessage.Should().Contain(activity.TraceId.ToString());
    }

    [Fact]
    public async Task Logs_None_Placeholder_When_No_CorrelationId_Present()
    {
        var logger = new RecordingLogger<UpstreamExceptionHandler>();
        var handler = new UpstreamExceptionHandler(new FakeProblemDetailsService(), logger);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/ping";

        // No CorrelationId in Items — the handler must not throw and must degrade gracefully.
        var handled = await handler.TryHandleAsync(ctx, new Exception("x"), CancellationToken.None);

        handled.Should().BeTrue();
        logger.LastMessage.Should().Contain("(none)");
    }

    private sealed class FakeProblemDetailsService : IProblemDetailsService
    {
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public string? LastMessage { get; private set; }
        public LogLevel LastLevel { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LastLevel = logLevel;
            LastMessage = formatter(state, exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
