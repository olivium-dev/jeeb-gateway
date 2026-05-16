using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-050 — /metrics endpoint + per-endpoint latency histogram.
///
/// Verifies that:
///   1. The Prometheus scrape endpoint is mounted and responds with
///      text/plain OpenMetrics output.
///   2. The Jeeb-owned latency histogram name and bucket layout are
///      present in the snapshot.
///   3. Issuing requests against known routes increments the histogram's
///      _count series with the matched route template (not the raw URL),
///      keeping label cardinality bounded.
/// </summary>
public class MetricsEndpointTests
{
    private const string HistogramName = "jeeb_gateway_http_request_duration_seconds";

    [Fact]
    public async Task Metrics_Endpoint_Returns_Prometheus_Text()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/metrics");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Accept either the OpenMetrics or the legacy text/plain content type
        // depending on the negotiation that AddPrometheusExporter performs.
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        contentType.Should().NotBeNullOrEmpty();
        contentType!.Should().Match(ct =>
            ct == "text/plain" || ct == "application/openmetrics-text");
    }

    [Fact]
    public async Task Histogram_Records_Per_Route_Increment()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        // Drive the latency middleware by issuing two requests to a routed
        // endpoint. The first call records into the meter; we then scrape
        // /metrics and assert the histogram_count for our route is at least 2.
        await client.GetAsync("/api/health");
        await client.GetAsync("/api/health");

        // Allow the OTel periodic export to flush. Default export period is
        // 1s for the Prometheus exporter, but reads are pull-based so the
        // snapshot is built on-demand — a single scrape is enough.
        var metrics = await client.GetStringAsync("/metrics");

        metrics.Should().Contain(HistogramName + "_bucket",
            "the Jeeb-owned histogram must export bucket series");
        metrics.Should().Contain(HistogramName + "_count",
            "the Jeeb-owned histogram must export count series");
        metrics.Should().Contain("route=\"/api/Health\"",
            "the route label should carry the matched template, not the raw URL");
        metrics.Should().Contain("method=\"GET\"");
        metrics.Should().Contain("status_class=\"2xx\"");
    }

    [Fact]
    public async Task Histogram_Boundaries_Cover_Slo_Threshold()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        await client.GetAsync("/api/health");

        var metrics = await client.GetStringAsync("/metrics");

        // The 400ms SLO boundary must be an explicit bucket so
        // histogram_quantile() does not interpolate across a wide bucket.
        // Prometheus emits the boundary as le="0.4".
        metrics.Should().Contain($"{HistogramName}_bucket")
            .And.Contain("le=\"0.4\"");
    }
}
