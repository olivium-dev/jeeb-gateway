using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Geolocation;

public sealed class GeoHistoryThrottleTests
{
    [Fact]
    public async Task Production_Typed_Client_Resolves_And_Location_Endpoint_Activates()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IGeoHistoryClient>()
            .Should().BeOfType<GeoHistoryClient>();

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        using var response = await http.PostAsJsonAsync("/location/update", new
        {
            points = Array.Empty<object>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the real LocationController dependency graph must activate before validating the empty batch");
    }

    [Fact]
    public async Task Write_Honors_RetryAfter_And_Persists_On_Bounded_Retry()
    {
        var handler = new ThrottleThenAcceptHandler(TimeSpan.FromMilliseconds(10));
        var client = Client(handler, maxThrottleDelayMs: 50);

        await client.RecordTrackPointAsync(
            "delivery-1", "courier-1", 33.9, 35.5, 4,
            DateTimeOffset.Parse("2026-08-06T09:14:20Z"));

        handler.Attempts.Should().Be(2);
        handler.Bodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task Four_Concurrent_Points_Honor_Throttle_Until_All_Are_Durable()
    {
        var handler = new IntervalRateLimitHandler(
            initialConcurrency: 4,
            interval: TimeSpan.FromMilliseconds(25));
        var client = Client(handler, maxThrottleDelayMs: 50, maxThrottleRetries: 4);
        var t0 = DateTimeOffset.Parse("2026-08-06T09:14:20Z");

        var writes = Enumerable.Range(0, 4)
            .Select(index => Task.Run(() => client.RecordTrackPointAsync(
                "delivery-1",
                "courier-1",
                33.9 + index / 1000d,
                35.5 + index / 1000d,
                5,
                t0.AddSeconds(index))))
            .ToArray();

        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(5));

        handler.Accepted.Should().Be(4,
            "each device-accepted fix must reach durable 30-day history within the bounded retry budget");
        handler.Throttled.Should().BeGreaterThan(0,
            "the positive control must exercise the same expected 429 path seen on MSI");
    }

    [Fact]
    public async Task Repeated_429s_Do_Not_Open_The_GeoHistory_Circuit()
    {
        var handler = new TwelveThrottlesThenAcceptHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddHttpClient("geo-history-test", http =>
            http.BaseAddress = new Uri("https://geo.test/"));
        builder.ConfigurePrimaryHttpMessageHandler(() => handler);
        ServiceClientExtensions.AttachGeoHistoryResilienceOnly(builder);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var http = factory.CreateClient("geo-history-test");

        for (var i = 0; i < 12; i++)
        {
            using var response = await http.GetAsync("probe");
            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        }

        using var recovery = await http.GetAsync("probe");
        recovery.StatusCode.Should().Be(HttpStatusCode.Created,
            "expected per-track throttles must not open a client-wide circuit");
        handler.Attempts.Should().Be(13,
            "every call, including the recovery probe, must reach geolocation");
    }

    [Fact]
    public void GeoHistory_Breaker_Excludes_429_But_Still_Counts_Real_Outages()
    {
        using var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        ServiceClientExtensions.ShouldBreakGeoHistory(null, throttled).Should().BeFalse();
        ServiceClientExtensions.ShouldBreakGeoHistory(null, unavailable).Should().BeTrue();
        ServiceClientExtensions.ShouldRetryGeoHistory(
            new Polly.CircuitBreaker.BrokenCircuitException(), null).Should().BeFalse();
    }

    private static GeoHistoryClient Client(
        HttpMessageHandler handler,
        int maxThrottleDelayMs,
        int maxThrottleRetries = 2)
    {
        return new GeoHistoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://geo.test/") },
            Options.Create(new GeoHistoryWriteOptions
            {
                MaxThrottleRetries = maxThrottleRetries,
                ThrottleFallbackDelayMs = 5,
                MaxThrottleDelayMs = maxThrottleDelayMs,
            }),
            NullLogger<GeoHistoryClient>.Instance);
    }

    private sealed class IntervalRateLimitHandler(
        int initialConcurrency,
        TimeSpan interval) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _initialBarrier = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialArrivals;
        private DateTimeOffset _nextEligible = DateTimeOffset.MinValue;

        public int Accepted { get; private set; }
        public int Throttled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _initialArrivals) <= initialConcurrency)
            {
                if (Volatile.Read(ref _initialArrivals) == initialConcurrency)
                    _initialBarrier.TrySetResult();
                await _initialBarrier.Task.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= _nextEligible)
                {
                    _nextEligible = now + interval;
                    Accepted++;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }

                Throttled++;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new RetryConditionHeaderValue(interval) },
                };
            }
        }
    }

    private sealed class ThrottleThenAcceptHandler(TimeSpan retryAfter) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Attempts == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new RetryConditionHeaderValue(retryAfter) },
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private sealed class TwelveThrottlesThenAcceptHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(
                Attempts <= 12 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Created));
        }
    }
}
