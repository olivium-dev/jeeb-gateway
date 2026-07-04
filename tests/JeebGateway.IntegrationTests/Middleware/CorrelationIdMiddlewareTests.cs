using FluentAssertions;
using JeebGateway.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests.Middleware;

/// <summary>
/// GW12-OBS-1 — <see cref="CorrelationIdMiddleware"/> unit tests. Beyond the
/// pre-existing wire behavior (mint-or-forward + response header), the id must now be
/// pushed into a log scope that is ACTIVE while the downstream pipeline runs, so every
/// log line emitted during the request carries the CorrelationId (captured by the OTel
/// log exporter's IncludeScopes).
/// </summary>
public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Forwards_Inbound_CorrelationId_Into_Items()
    {
        var logger = new RecordingLogger<CorrelationIdMiddleware>();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Correlation-Id"] = "client-supplied-id";

        var mw = new CorrelationIdMiddleware(_ => Task.CompletedTask, logger);
        await mw.InvokeAsync(ctx);

        ctx.Items["CorrelationId"].Should().Be("client-supplied-id");
    }

    [Fact]
    public async Task Mints_CorrelationId_When_Absent()
    {
        var logger = new RecordingLogger<CorrelationIdMiddleware>();
        var ctx = new DefaultHttpContext();

        var mw = new CorrelationIdMiddleware(_ => Task.CompletedTask, logger);
        await mw.InvokeAsync(ctx);

        var minted = ctx.Items["CorrelationId"] as string;
        minted.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(minted, out _).Should().BeTrue("a minted id is a GUID");
    }

    [Fact]
    public async Task Pushes_CorrelationId_Into_An_Active_Log_Scope_During_Pipeline()
    {
        var logger = new RecordingLogger<CorrelationIdMiddleware>();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Correlation-Id"] = "scope-id-42";

        object? scopeDuringNext = null;
        var mw = new CorrelationIdMiddleware(
            _ =>
            {
                // The scope must be open while the downstream delegate runs.
                scopeDuringNext = logger.CurrentScope;
                return Task.CompletedTask;
            },
            logger);

        await mw.InvokeAsync(ctx);

        scopeDuringNext.Should().NotBeNull("a log scope must be active during the pipeline");
        var dict = scopeDuringNext as IReadOnlyDictionary<string, object>;
        dict.Should().NotBeNull();
        dict!["CorrelationId"].Should().Be("scope-id-42");

        // Scope is disposed once the request completes.
        logger.CurrentScope.Should().BeNull();
    }

    /// <summary>Minimal ILogger that records the most-recently-opened (and not-yet-disposed) scope.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public object? CurrentScope { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            CurrentScope = state;
            return new ScopeToken(this);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class ScopeToken : IDisposable
        {
            private readonly RecordingLogger<T> _owner;
            public ScopeToken(RecordingLogger<T> owner) => _owner = owner;
            public void Dispose() => _owner.CurrentScope = null;
        }
    }
}
