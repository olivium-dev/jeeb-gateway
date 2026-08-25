using System.Net;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace JeebGateway.Realtime.Proxy;

/// <summary>
/// A single pooled, no-cookie, no-proxy, no-retry transport dedicated to the
/// WebSocket route. YARP's activity timeout is idle-based, so active Phoenix
/// heartbeat and message traffic keeps the connection alive.
/// </summary>
internal sealed class RealtimeWebSocketProxyTransport : IDisposable
{
    public HttpMessageInvoker Invoker { get; }
    public ForwarderRequestConfig RequestConfig { get; }

    public RealtimeWebSocketProxyTransport(
        IOptions<RealtimeWebSocketProxyOptions> options)
    {
        var value = options.Value;
        var globalLimit = Math.Clamp(value.GlobalConcurrencyLimit, 1, 4096);
        var connectSeconds = Math.Clamp(value.ConnectTimeoutSeconds, 1, 30);
        var activitySeconds = Math.Clamp(value.ActivityTimeoutSeconds, 15, 300);

        var handler = new SocketsHttpHandler
        {
            ActivityHeadersPropagator = null,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(connectSeconds),
            MaxConnectionsPerServer = globalLimit,
            UseCookies = false,
            UseProxy = false,
        };

        Invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        RequestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(activitySeconds),
            AllowResponseBuffering = false,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    public void Dispose() => Invoker.Dispose();
}
