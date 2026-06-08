using System.Net;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.StateService.Durable;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-50 (S05 H9b): unit coverage for the broadcast-event recorder — the
/// gateway side of the OWNER-DIRECTIVE "broadcast event MUST be LOGGED to the
/// bundler". Proves it (a) POSTs to the documented route
/// <c>v1/state/broadcasts</c> with the <c>{contextId, phase, source}</c> body,
/// (b) reports <c>Recorded</c> on 2xx, and (c) DEGRADES to <c>Unavailable</c> on
/// a 5xx / transport failure rather than throwing onto the order create.
/// </summary>
public sealed class BroadcastEventRecorderTests
{
    [Fact]
    public async Task Posts_contextId_phase_and_source_to_broadcasts_route()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created);
        var recorder = NewRecorder(handler);

        var outcome = await recorder.RecordBroadcastingAsync("conv-123", "broadcasting", CancellationToken.None);

        outcome.Should().Be(BroadcastEventRecordOutcome.Recorded);
        handler.LastRequestUri!.AbsolutePath.Should().EndWith("/v1/state/broadcasts");
        handler.LastMethod.Should().Be(HttpMethod.Post);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        root.GetProperty("contextId").GetString().Should().Be("conv-123");
        root.GetProperty("phase").GetString().Should().Be("broadcasting");
        root.GetProperty("source").GetString().Should().Be("jeeb-gateway");
    }

    [Fact]
    public async Task Degrades_to_unavailable_on_server_error()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var recorder = NewRecorder(handler);

        var outcome = await recorder.RecordBroadcastingAsync("conv-123", "broadcasting", CancellationToken.None);

        outcome.Should().Be(BroadcastEventRecordOutcome.Unavailable);
    }

    [Fact]
    public async Task Degrades_to_unavailable_on_transport_failure()
    {
        var handler = new ThrowingHandler();
        var recorder = NewRecorder(handler);

        var outcome = await recorder.RecordBroadcastingAsync("conv-123", "broadcasting", CancellationToken.None);

        // A state-service outage must not throw onto the order create path.
        outcome.Should().Be(BroadcastEventRecordOutcome.Unavailable);
    }

    private static StateServiceBroadcastEventRecorder NewRecorder(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://state-svc.local/") };
        return new StateServiceBroadcastEventRecorder(
            http, NullLogger<StateServiceBroadcastEventRecorder>.Instance);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        public CapturingHandler(HttpStatusCode status) => _status = status;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("state-service unreachable");
    }
}
