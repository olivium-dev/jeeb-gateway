using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Realtime;
using JeebGateway.Realtime.Proxy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Realtime;

public sealed class RealtimeWebSocketProxyTests
{
    private const string Marker = RealtimeWebSocketProxyTransformer.MarkerHeader;
    private const string ExactOrigin = RealtimeWebSocketProxyTransformer.ExactBrowserOrigin;

    [Fact]
    public async Task A1_disabled_route_is_404_and_never_resolves_a_destination()
    {
        var resolver = new RecordingDestinationResolver("http://127.0.0.1:1");
        await using var gateway = await RunningApp.StartGatewayAsync(
            enabled: false,
            resolver: resolver);

        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };
        var response = await client.GetAsync("/socket/websocket?token=must-not-dial");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resolver.ResolveCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/Socket/websocket")]
    [InlineData("/socket/WebSocket")]
    [InlineData("/socket/websocket/")]
    [InlineData("/socket/websocket/extra")]
    public async Task Only_the_exact_case_sensitive_path_can_dial(string path)
    {
        var resolver = new RecordingDestinationResolver("http://127.0.0.1:1");
        await using var gateway = await RunningApp.StartGatewayAsync(
            enabled: true,
            resolver: resolver);

        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };
        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resolver.ResolveCount.Should().Be(0);
    }

    [Fact]
    public async Task Non_GET_exact_path_is_not_forwarded()
    {
        var resolver = new RecordingDestinationResolver("http://127.0.0.1:1");
        await using var gateway = await RunningApp.StartGatewayAsync(
            enabled: true,
            resolver: resolver);

        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };
        var response = await client.PostAsync("/socket/websocket", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        resolver.ResolveCount.Should().Be(0);
    }

    [Fact]
    public async Task Enabled_invalid_overlay_config_is_sanitized_503_while_health_stays_green()
    {
        await using var gateway = await RunningApp.StartGatewayAsync(
            enabled: true,
            configuredRealtimeBaseUrl: "https://external.invalid:4000/",
            resolver: null);

        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };
        var response = await client.GetAsync("/socket/websocket?token=sentinel-secret");
        var body = await response.Content.ReadAsStringAsync();
        var health = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues(Marker).Should().Equal("gateway");
        body.Should().NotContain("external.invalid");
        body.Should().NotContain("sentinel-secret");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enabled_outside_Staging_hard_fails_at_route_mapping()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.AddInMemoryCollection(Configuration(enabled: true));
        builder.Services.Configure<RealtimeGuardianOptions>(options =>
            options.BaseUrl = "http://jeeb-staging-realtime-comunication-service:4000");
        builder.Services.AddRealtimeWebSocketProxy(builder.Configuration);
        var app = builder.Build();

        var action = () => app.MapRealtimeWebSocketProxy(app.Environment);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*may be true only in Staging*");
        await app.DisposeAsync();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Upstream_auth_status_and_gateway_marker_are_preserved_but_unsafe_headers_are_removed(
        HttpStatusCode upstreamStatus)
    {
        var upstreamHits = 0;
        var observation = new TaskCompletionSource<ObservedRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await RunningApp.StartUpstreamAsync(async context =>
        {
            Interlocked.Increment(ref upstreamHits);
            observation.TrySetResult(ObservedRequest.Capture(context));
            context.Response.StatusCode = (int)upstreamStatus;
            context.Response.Headers.Append("Set-Cookie", "session=upstream-secret");
            context.Response.Headers.Append("Server", "unsafe-upstream");
            context.Response.Headers.Append("X-Powered-By", "unsafe-upstream");
            context.Response.Headers.Append("X-Internal-Token", "upstream-secret");
            context.Response.Headers.Append("X-User-Id", "spoofed-user");
            context.Response.Headers.Append(Marker, "spoofed-upstream");
            await context.Response.WriteAsync("upstream token=sentinel-secret");
        });
        var resolver = new RecordingDestinationResolver(upstream.BaseAddress.ToString().TrimEnd('/'));
        await using var gateway = await RunningApp.StartGatewayAsync(true, resolver);

        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/socket/websocket?vsn=2.0.0&token=a%2Bb%2Fc&token=second&ticket=x%20y");
        request.Headers.TryAddWithoutValidation("Origin", ExactOrigin);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer inbound-secret");
        request.Headers.TryAddWithoutValidation("Cookie", "session=inbound-secret");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "inbound-secret");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
        request.Headers.TryAddWithoutValidation(Marker, "spoofed-client");
        request.Headers.TryAddWithoutValidation("X-Internal-Token", "inbound-secret");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var observed = await observation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        response.StatusCode.Should().Be(upstreamStatus);
        response.Headers.GetValues(Marker).Should().Equal("gateway");
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse();
        response.Headers.TryGetValues("Server", out _).Should().BeFalse();
        response.Headers.TryGetValues("X-Powered-By", out _).Should().BeFalse();
        response.Headers.TryGetValues("X-Internal-Token", out _).Should().BeFalse();
        response.Headers.TryGetValues("X-User-Id", out _).Should().BeFalse();
        body.Should().NotContain("sentinel-secret");
        Volatile.Read(ref upstreamHits).Should().Be(1, "the transport never retries");
        observed.RawTarget.Should().Be(
            "/socket/websocket?vsn=2.0.0&token=a%2Bb%2Fc&token=second&ticket=x%20y");
        observed.Headers[Marker].Should().Equal("gateway");
        observed.Headers.Should().NotContainKeys(
            "Authorization",
            "Cookie",
            "X-Api-Key",
            "X-Forwarded-For",
            "X-Internal-Token");
    }

    [Fact]
    public async Task Origin_is_optional_or_exact_and_any_other_value_is_rejected_without_a_dial()
    {
        var hits = 0;
        await using var upstream = await RunningApp.StartUpstreamAsync(context =>
        {
            Interlocked.Increment(ref hits);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
        var resolver = new RecordingDestinationResolver(upstream.BaseAddress.ToString().TrimEnd('/'));
        await using var gateway = await RunningApp.StartGatewayAsync(true, resolver);
        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };

        (await client.GetAsync("/socket/websocket")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        using (var exact = new HttpRequestMessage(HttpMethod.Get, "/socket/websocket"))
        {
            exact.Headers.TryAddWithoutValidation("Origin", ExactOrigin);
            (await client.SendAsync(exact)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        using (var foreign = new HttpRequestMessage(HttpMethod.Get, "/socket/websocket"))
        {
            foreign.Headers.TryAddWithoutValidation("Origin", "https://evil.invalid");
            (await client.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        Volatile.Read(ref hits).Should().Be(2);
    }

    [Fact]
    public async Task Real_Kestrel_101_tunnels_heartbeat_join_and_upstream_membership_denials()
    {
        var requestObserved = new TaskCompletionSource<ObservedRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await RunningApp.StartPhoenixUpstreamAsync(requestObserved);
        var resolver = new RecordingDestinationResolver(upstream.BaseAddress.ToString().TrimEnd('/'));
        await using var gateway = await RunningApp.StartGatewayAsync(true, resolver);
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", ExactOrigin);
        socket.Options.SetRequestHeader("Authorization", "Bearer must-be-stripped");
        socket.Options.SetRequestHeader(Marker, "spoofed-client");
        var wsUri = new UriBuilder(gateway.BaseAddress)
        {
            Scheme = "ws",
            Path = "/socket/websocket",
            Query = "vsn=2.0.0&token=connect%2Btoken&token=duplicate",
        }.Uri;

        await socket.ConnectAsync(wsUri, CancellationToken.None);
        socket.State.Should().Be(WebSocketState.Open);
        socket.HttpResponseHeaders.Should().ContainKey(Marker);
        socket.HttpResponseHeaders![Marker].Should().Equal("gateway");

        await SendTextAsync(socket, "[\"1\",\"1\",\"phoenix\",\"heartbeat\",{}]");
        (await ReceiveTextAsync(socket)).Should().Contain("heartbeat");

        await SendTextAsync(socket,
            "[\"2\",\"2\",\"jeeb:chat:allowed\",\"phx_join\",{\"ticket\":\"valid\"}]");
        (await ReceiveTextAsync(socket)).Should().Contain("\"status\":\"ok\"");

        await SendTextAsync(socket,
            "[\"3\",\"3\",\"jeeb:chat:other\",\"phx_join\",{\"ticket\":\"valid\"}]");
        (await ReceiveTextAsync(socket)).Should().Contain("forbidden");

        await SendTextAsync(socket,
            "[\"4\",\"4\",\"jeeb:chat:allowed\",\"phx_join\",{\"ticket\":\"forged\"}]");
        (await ReceiveTextAsync(socket)).Should().Contain("not_in_membership");

        var observed = await requestObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.RawTarget.Should().Be(
            "/socket/websocket?vsn=2.0.0&token=connect%2Btoken&token=duplicate");
        observed.Headers[Marker].Should().Equal("gateway");
        observed.Headers.Should().NotContainKey("Authorization");

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None);
    }

    [Fact]
    public void Concurrency_is_bounded_globally_and_per_client_ip_without_a_queue()
    {
        var limiter = new RealtimeWebSocketProxyConcurrencyLimiter(
            Options.Create(new RealtimeWebSocketProxyOptions
            {
                GlobalConcurrencyLimit = 2,
                PerIpConcurrencyLimit = 1,
                MaximumTrackedClientIps = 64,
            }));
        var firstIp = Context("203.0.113.10");
        var secondIp = Context("203.0.113.11");

        using var first = limiter.TryAcquire(firstIp);
        first.Should().NotBeNull();
        limiter.TryAcquire(firstIp).Should().BeNull("one IP has one permit");
        using var second = limiter.TryAcquire(secondIp);
        second.Should().NotBeNull("a different IP has its own partition");
        limiter.TryAcquire(Context("203.0.113.12")).Should().BeNull(
            "the global limit is also enforced");
    }

    [Fact]
    public void Distinct_client_partitions_are_strictly_capped_with_conservative_overflow()
    {
        var limiter = new RealtimeWebSocketProxyConcurrencyLimiter(
            Options.Create(new RealtimeWebSocketProxyOptions
            {
                GlobalConcurrencyLimit = 66,
                PerIpConcurrencyLimit = 1,
                MaximumTrackedClientIps = 64,
            }));
        var leases = Enumerable.Range(1, 64)
            .Select(index => limiter.TryAcquire(Context($"203.0.113.{index}")))
            .ToArray();

        leases.Should().OnlyContain(lease => lease != null);
        using var overflow = limiter.TryAcquire(Context("198.51.100.1"));
        overflow.Should().NotBeNull();
        limiter.TryAcquire(Context("198.51.100.2")).Should().BeNull(
            "untracked IPs share the bounded overflow partition");

        foreach (var lease in leases)
        {
            lease!.Dispose();
        }
    }

    [Fact]
    public async Task Real_WebSocket_disconnect_releases_the_per_ip_and_global_lease()
    {
        var observation = new TaskCompletionSource<ObservedRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await RunningApp.StartPhoenixUpstreamAsync(observation);
        var resolver = new RecordingDestinationResolver(upstream.BaseAddress.ToString().TrimEnd('/'));
        await using var gateway = await RunningApp.StartGatewayAsync(
            enabled: true,
            resolver: resolver,
            globalConcurrencyLimit: 1,
            perIpConcurrencyLimit: 1);
        var wsUri = new UriBuilder(gateway.BaseAddress)
        {
            Scheme = "ws",
            Path = RealtimeWebSocketProxyEndpoint.Route,
        }.Uri;

        using var first = NewClientWebSocket();
        await first.ConnectAsync(wsUri, CancellationToken.None);

        using var http = new HttpClient { BaseAddress = gateway.BaseAddress };
        var rejected = await http.GetAsync(RealtimeWebSocketProxyEndpoint.Route);
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.GetValues(Marker).Should().Equal("gateway");

        await first.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "release",
            CancellationToken.None);

        using var afterDisconnect = NewClientWebSocket();
        await afterDisconnect.ConnectAsync(wsUri, CancellationToken.None);
        afterDisconnect.State.Should().Be(WebSocketState.Open);
        await afterDisconnect.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None);
    }

    [Fact]
    public async Task Dial_failure_is_single_attempt_sanitized_and_never_reflects_query_credentials()
    {
        var resolver = new RecordingDestinationResolver("http://127.0.0.1:1");
        await using var gateway = await RunningApp.StartGatewayAsync(true, resolver);
        using var client = new HttpClient { BaseAddress = gateway.BaseAddress };

        var response = await client.GetAsync(
            "/socket/websocket?token=sentinel-secret&ticket=sentinel-membership");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.GetValues(Marker).Should().Equal("gateway");
        body.Should().NotContain("sentinel-secret");
        body.Should().NotContain("sentinel-membership");
        body.Should().NotContain("127.0.0.1");
        resolver.ResolveCount.Should().Be(1);
    }

    [Fact]
    public void Transport_and_telemetry_exclusion_are_bounded_to_the_sensitive_route()
    {
        using var transport = new RealtimeWebSocketProxyTransport(
            Options.Create(new RealtimeWebSocketProxyOptions
            {
                GlobalConcurrencyLimit = 8,
                ConnectTimeoutSeconds = 2,
                ActivityTimeoutSeconds = 15,
            }));

        transport.RequestConfig.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(15));
        transport.RequestConfig.AllowResponseBuffering.Should().BeFalse();
        transport.RequestConfig.Version.Should().Be(HttpVersion.Version11);
        transport.RequestConfig.VersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionExact);
        RealtimeWebSocketProxyEndpoint.IsSensitivePath("/socket/websocket").Should().BeTrue();
        RealtimeWebSocketProxyEndpoint.IsSensitivePath("/Socket/WebSocket/extra")
            .Should().BeTrue();
        RealtimeWebSocketProxyEndpoint.IsSensitivePath("/auth/tokens").Should().BeFalse();
    }

    private static DefaultHttpContext Context(string ip)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    private static ClientWebSocket NewClientWebSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        return socket;
    }

    private static Dictionary<string, string?> Configuration(
        bool enabled,
        int globalConcurrencyLimit = 8,
        int perIpConcurrencyLimit = 2) => new()
        {
            [$"{RealtimeWebSocketProxyOptions.SectionName}:Enabled"] = enabled.ToString(),
            [$"{RealtimeWebSocketProxyOptions.SectionName}:GlobalConcurrencyLimit"] =
            globalConcurrencyLimit.ToString(),
            [$"{RealtimeWebSocketProxyOptions.SectionName}:PerIpConcurrencyLimit"] =
            perIpConcurrencyLimit.ToString(),
            [$"{RealtimeWebSocketProxyOptions.SectionName}:MaximumTrackedClientIps"] = "64",
            [$"{RealtimeWebSocketProxyOptions.SectionName}:ConnectTimeoutSeconds"] = "2",
            [$"{RealtimeWebSocketProxyOptions.SectionName}:ActivityTimeoutSeconds"] = "15",
        };

    private static async Task SendTextAsync(ClientWebSocket socket, string text) =>
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket)
    {
        var bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes, CancellationToken.None);
        return Encoding.UTF8.GetString(bytes, 0, result.Count);
    }

    private sealed class RecordingDestinationResolver(string destinationPrefix)
        : IRealtimeProxyDestinationResolver
    {
        private int _resolveCount;
        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public bool TryResolve(out string value)
        {
            Interlocked.Increment(ref _resolveCount);
            value = destinationPrefix;
            return true;
        }
    }

    private sealed record ObservedRequest(
        string RawTarget,
        IReadOnlyDictionary<string, string[]> Headers)
    {
        public static ObservedRequest Capture(HttpContext context) => new(
            context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? string.Empty,
            context.Request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private sealed class RunningApp(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<RunningApp> StartGatewayAsync(
            bool enabled,
            IRealtimeProxyDestinationResolver? resolver,
            string configuredRealtimeBaseUrl = "http://jeeb-staging-realtime-comunication-service:4000",
            int globalConcurrencyLimit = 8,
            int perIpConcurrencyLimit = 2)
        {
            var builder = NewBuilder(Environments.Staging);
            builder.Configuration.AddInMemoryCollection(Configuration(
                enabled,
                globalConcurrencyLimit,
                perIpConcurrencyLimit));
            builder.Services.Configure<RealtimeGuardianOptions>(options =>
                options.BaseUrl = configuredRealtimeBaseUrl);
            builder.Services.AddRealtimeWebSocketProxy(builder.Configuration);
            if (resolver is not null)
            {
                builder.Services.RemoveAll<IRealtimeProxyDestinationResolver>();
                builder.Services.AddSingleton(resolver);
            }

            var app = builder.Build();
            app.MapRealtimeWebSocketProxy(app.Environment);
            app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
            await app.StartAsync();
            return FromStarted(app);
        }

        public static async Task<RunningApp> StartUpstreamAsync(RequestDelegate request)
        {
            var builder = NewBuilder(Environments.Staging);
            var app = builder.Build();
            app.Map(RealtimeWebSocketProxyEndpoint.Route, request);
            await app.StartAsync();
            return FromStarted(app);
        }

        public static async Task<RunningApp> StartPhoenixUpstreamAsync(
            TaskCompletionSource<ObservedRequest> observation)
        {
            var builder = NewBuilder(Environments.Staging);
            var app = builder.Build();
            app.UseWebSockets();
            app.Map(RealtimeWebSocketProxyEndpoint.Route, async context =>
            {
                observation.TrySetResult(ObservedRequest.Capture(context));
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var bytes = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(bytes, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "done",
                            CancellationToken.None);
                        return;
                    }

                    var request = Encoding.UTF8.GetString(bytes, 0, result.Count);
                    string response;
                    if (request.Contains("\"heartbeat\"", StringComparison.Ordinal))
                    {
                        response = request;
                    }
                    else if (request.Contains("jeeb:chat:other", StringComparison.Ordinal))
                    {
                        response = "[\"3\",\"3\",\"jeeb:chat:other\",\"phx_reply\",{\"status\":\"error\",\"response\":{\"reason\":\"forbidden\"}}]";
                    }
                    else if (request.Contains("\"ticket\":\"forged\"", StringComparison.Ordinal))
                    {
                        response = "[\"4\",\"4\",\"jeeb:chat:allowed\",\"phx_reply\",{\"status\":\"error\",\"response\":{\"reason\":\"not_in_membership\"}}]";
                    }
                    else
                    {
                        response = "[\"2\",\"2\",\"jeeb:chat:allowed\",\"phx_reply\",{\"status\":\"ok\",\"response\":{}}]";
                    }

                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(response),
                        WebSocketMessageType.Text,
                        true,
                        context.RequestAborted);
                }
            });
            await app.StartAsync();
            return FromStarted(app);
        }

        private static WebApplicationBuilder NewBuilder(string environment)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environment,
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            return builder;
        }

        private static RunningApp FromStarted(WebApplication app)
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            return new RunningApp(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
