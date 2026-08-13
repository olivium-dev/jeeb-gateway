using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JeebGateway.Migration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// gwdbx W3-02 — queues escalation rows for a best-effort POST to delivery-service
/// <c>/api/v1/escalations</c> behind <c>FeatureFlags:OtpEscalationsMode</c>.
///
/// <para><b>G-11.</b> <see cref="MirrorAsync"/> is a non-blocking
/// <see cref="Channel"/> write and returns an already-completed task; the HTTP call
/// happens on <see cref="EscalationMirrorDrainer"/>. A dead or slow delivery-service
/// therefore cannot add a microsecond to the 423 path. The queue is BOUNDED and
/// drops the oldest entry when full — a mirror gap beats back-pressure on a lockout,
/// and every eviction is logged so the gap is never silent.</para>
///
/// <para><b>G-15.</b> The idempotency key is <c>AdminEscalation.Id</c>, sent as the
/// upstream <c>escalation_id</c>: a replay returns the stored row, never a duplicate.</para>
/// </summary>
public sealed class DeliveryServiceEscalationMirror : IEscalationMirror
{
    // Bounded so a long upstream outage cannot grow the gateway's heap.
    public const int QueueCapacity = 512;

    private readonly Channel<AdminEscalation> _queue;

    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<DeliveryServiceEscalationMirror> _log;

    public DeliveryServiceEscalationMirror(
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<DeliveryServiceEscalationMirror> log)
    {
        _mode = mode;
        _log = log;
        _queue = Channel.CreateBounded<AdminEscalation>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            OnEvicted);
    }

    // DropOldest returns TryWrite==true while discarding a row, so without this callback
    // the bounded loss is invisible: the warning below is the only trace of a mirror gap.
    private void OnEvicted(AdminEscalation row) =>
        _log.LogWarning(
            "escalation mirror EVICTED escalationId={EscalationId} deliveryId={DeliveryId}: " +
            "queue full at {Capacity}; the local row stays authoritative and W3-07 replays this id.",
            row.Id, row.DeliveryId, QueueCapacity);

    /// <summary>Rows waiting to be mirrored; the drainer is the only reader.</summary>
    public ChannelReader<AdminEscalation> Reader => _queue.Reader;

    /// <inheritdoc />
    public Task MirrorAsync(AdminEscalation row, CancellationToken ct)
    {
        if (row is null || _mode.CurrentValue.OtpEscalations < GwdbxMigrationPhase.DualWriteLocalRead)
        {
            return Task.CompletedTask;
        }

        // TryWrite never blocks: DropOldest evicts rather than waiting for room.
        if (!_queue.Writer.TryWrite(row))
        {
            _log.LogWarning(
                "escalation mirror DROPPED escalationId={EscalationId} deliveryId={DeliveryId}: queue closed.",
                row.Id, row.DeliveryId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Drains <see cref="DeliveryServiceEscalationMirror"/> off the request path and POSTs
/// each row to delivery-service. Failures are logged and dropped — the local row stays
/// authoritative and the W3-07 backfill replays the same idempotency key.
/// </summary>
public sealed class EscalationMirrorDrainer : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeliveryServiceEscalationMirror _mirror;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<EscalationMirrorDrainer> _log;

    /// <summary>Named client configured with the delivery-service base address.</summary>
    public const string HttpClientName = "EscalationMirror";

    public EscalationMirrorDrainer(
        DeliveryServiceEscalationMirror mirror,
        IHttpClientFactory http,
        ILogger<EscalationMirrorDrainer> log)
    {
        _mirror = mirror;
        _http = http;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var row in _mirror.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await PostAsync(row, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "escalation mirror POST failed for escalationId={EscalationId} deliveryId={DeliveryId}; " +
                    "the local admin_escalations row is authoritative and the W3-07 backfill replays this id.",
                    row.Id, row.DeliveryId);
            }
        }
    }

    private async Task PostAsync(AdminEscalation row, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync("api/v1/escalations", new EscalationMirrorRequest
        {
            // G-15 — the idempotency key IS admin_escalations.id.
            EscalationId = row.Id,
            DeliveryId = row.DeliveryId,
            ClientId = row.ClientId,
            // G-28 — holder-generic wire name; the gateway's jeeberId is the provider.
            ProviderId = row.JeeberId,
            Reason = row.Reason,
            Status = row.Status,
            AttemptCount = row.OtpAttemptCount,
            CreatedAt = row.CreatedAt,
        }, Json, ct);

        // 200 = idempotent replay of a row already mirrored; both are success.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            _log.LogWarning(
                "escalation mirror POST returned {Status} for escalationId={EscalationId}.",
                (int)response.StatusCode, row.Id);
        }
    }
}

/// <summary>Wire shape of delivery-service <c>POST /api/v1/escalations</c> (G-28 neutral).</summary>
public sealed class EscalationMirrorRequest
{
    [JsonPropertyName("escalation_id")] public required string EscalationId { get; init; }
    [JsonPropertyName("delivery_id")] public required string DeliveryId { get; init; }
    [JsonPropertyName("client_id")] public required string ClientId { get; init; }
    [JsonPropertyName("provider_id")] public string? ProviderId { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("attempt_count")] public int AttemptCount { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}
