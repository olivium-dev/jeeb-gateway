using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JeebGateway.Migration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Availability;

/// <summary>The two signals this mirror forwards. Neither decides presence upstream.</summary>
public sealed record AvailabilityMirrorSignal(string UserId, bool IdleOffline);

/// <summary>
/// gwdbx W3-04 — queues availability signals for a best-effort POST to
/// delivery-service behind <c>FeatureFlags:AvailabilityMode</c>. Inert at the
/// default "local" rung, so the shipped default changes nothing.
///
/// <para><b>G-11.</b> <c>Mirror*Async</c> is a non-blocking <see cref="Channel"/>
/// write returning an already-completed task; the HTTP call happens on
/// <see cref="AvailabilityMirrorDrainer"/>. A dead or slow delivery-service can
/// therefore not add a microsecond to a jeeber's availability read or to a sweep.
/// The queue is BOUNDED and drops the oldest entry when full; every eviction is
/// logged so the gap is never silent.</para>
/// </summary>
public sealed class DeliveryServiceAvailabilityMirror : IAvailabilityMirror
{
    // Bounded so a long upstream outage cannot grow the gateway's heap.
    public const int QueueCapacity = 512;

    private readonly Channel<AvailabilityMirrorSignal> _queue;

    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<DeliveryServiceAvailabilityMirror> _log;

    public DeliveryServiceAvailabilityMirror(
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<DeliveryServiceAvailabilityMirror> log)
    {
        _mode = mode;
        _log = log;
        _queue = Channel.CreateBounded<AvailabilityMirrorSignal>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            OnEvicted);
    }

    // DropOldest returns TryWrite==true while discarding a signal, so without this
    // callback the bounded loss is invisible.
    private void OnEvicted(AvailabilityMirrorSignal signal) =>
        _log.LogWarning(
            "availability mirror EVICTED jeeberId={JeeberId} idleOffline={IdleOffline}: queue full at " +
            "{Capacity}; the gateway store stays authoritative and W3-07 re-imports this row.",
            signal.UserId, signal.IdleOffline, QueueCapacity);

    /// <summary>Signals waiting to be mirrored; the drainer is the only reader.</summary>
    public ChannelReader<AvailabilityMirrorSignal> Reader => _queue.Reader;

    /// <inheritdoc />
    public Task MirrorInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct) =>
        Enqueue(new AvailabilityMirrorSignal(userId, IdleOffline: false));

    /// <inheritdoc />
    public Task MirrorIdleOfflineAsync(string userId, CancellationToken ct) =>
        Enqueue(new AvailabilityMirrorSignal(userId, IdleOffline: true));

    private Task Enqueue(AvailabilityMirrorSignal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.UserId)
            || _mode.CurrentValue.Availability < GwdbxMigrationPhase.DualWriteLocalRead)
        {
            return Task.CompletedTask;
        }

        // TryWrite never blocks: DropOldest evicts rather than waiting for room.
        if (!_queue.Writer.TryWrite(signal))
        {
            _log.LogWarning(
                "availability mirror DROPPED jeeberId={JeeberId}: queue closed.", signal.UserId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Drains <see cref="DeliveryServiceAvailabilityMirror"/> off the request path and
/// POSTs each signal to delivery-service. Failures are logged and dropped — the
/// gateway store stays authoritative and the W3-07 import re-converges the row.
/// </summary>
public sealed class AvailabilityMirrorDrainer : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeliveryServiceAvailabilityMirror _mirror;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AvailabilityMirrorDrainer> _log;

    /// <summary>Named client configured with the delivery-service base address.</summary>
    public const string HttpClientName = "AvailabilityMirror";

    public AvailabilityMirrorDrainer(
        DeliveryServiceAvailabilityMirror mirror,
        IHttpClientFactory http,
        ILogger<AvailabilityMirrorDrainer> log)
    {
        _mirror = mirror;
        _http = http;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var signal in _mirror.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await PostAsync(signal, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "availability mirror POST failed for jeeberId={JeeberId} idleOffline={IdleOffline}; " +
                    "the gateway store is authoritative and the W3-07 import re-converges this row.",
                    signal.UserId, signal.IdleOffline);
            }
        }
    }

    private async Task PostAsync(AvailabilityMirrorSignal signal, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        var id = Uri.EscapeDataString(signal.UserId);

        // G-28 neutral /providers vocabulary. The idle flip carries interaction=false
        // because a sweeper flip is NOT user activity; a 404 (upstream has never seen
        // this jeeber) is an expected pre-backfill outcome, not an error.
        using var response = signal.IdleOffline
            ? await client.PostAsJsonAsync(
                $"providers/{id}/availability",
                new AvailabilityMirrorOfflineRequest(), Json, ct)
            : await client.PostAsJsonAsync($"providers/{id}/interaction", new { }, Json, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            _log.LogWarning(
                "availability mirror POST returned {Status} for jeeberId={JeeberId}.",
                (int)response.StatusCode, signal.UserId);
        }
    }
}

/// <summary>Wire shape of the sweeper's idle flip (delivery-service snake_case).</summary>
public sealed class AvailabilityMirrorOfflineRequest
{
    [JsonPropertyName("online")] public bool Online => false;

    [JsonPropertyName("interaction")] public bool Interaction => false;
}
