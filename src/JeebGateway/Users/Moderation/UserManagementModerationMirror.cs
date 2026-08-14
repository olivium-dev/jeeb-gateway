using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JeebGateway.Migration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.Moderation;

/// <summary>
/// gwdbx W4-04 — queues suspension flips for a best-effort POST to
/// user-management's moderation surface (<c>/api/User/{id}/moderation/*</c>,
/// W4-03) behind <c>FeatureFlags:UserModerationMode</c>.
///
/// <para>Below <c>dual-write-local-read</c> the mirror is a strict no-op, so
/// shipping this is behaviour-neutral. The enqueue is a non-blocking bounded
/// <see cref="Channel"/> write (escalation-mirror shape, G-11): a dead or slow
/// user-management cannot slow the admin suspend path, and an eviction is
/// logged so a mirror gap is never silent — the W4-05 backfill replays state.</para>
///
/// <para>Idempotency: upstream suspend/unsuspend are state-idempotent (W4-03),
/// so replaying the latest flip converges; ordering races between two admin
/// flips are resolved by the backfill's caller-stamped state.</para>
/// </summary>
public sealed class UserManagementModerationMirror : IUserModerationMirror
{
    // Bounded so a long upstream outage cannot grow the gateway's heap.
    public const int QueueCapacity = 256;

    private readonly Channel<UserModerationChange> _queue;

    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<UserManagementModerationMirror> _log;

    public UserManagementModerationMirror(
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<UserManagementModerationMirror> log)
    {
        _mode = mode;
        _log = log;
        _queue = Channel.CreateBounded<UserModerationChange>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            OnEvicted);
    }

    // DropOldest returns TryWrite==true while discarding a row; this callback is
    // the only trace of the gap, so it must log rather than stay silent.
    private void OnEvicted(UserModerationChange change) =>
        _log.LogWarning(
            "moderation mirror EVICTED userId={UserId} suspended={IsSuspended}: queue full at {Capacity}; " +
            "the local projection stays authoritative and the W4-05 backfill replays this state.",
            change.UserId, change.IsSuspended, QueueCapacity);

    /// <summary>Changes waiting to be mirrored; the drainer is the only reader.</summary>
    public ChannelReader<UserModerationChange> Reader => _queue.Reader;

    /// <inheritdoc />
    public Task MirrorAsync(UserModerationChange change, CancellationToken ct)
    {
        if (change is null || _mode.CurrentValue.UserModeration < GwdbxMigrationPhase.DualWriteLocalRead)
        {
            return Task.CompletedTask;
        }

        if (!_queue.Writer.TryWrite(change))
        {
            _log.LogWarning(
                "moderation mirror DROPPED userId={UserId} suspended={IsSuspended}: queue closed.",
                change.UserId, change.IsSuspended);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Drains <see cref="UserManagementModerationMirror"/> off the request path and
/// POSTs each flip to user-management. Failures are logged and dropped — the
/// local projection stays authoritative and W4-05 replays the state.
/// </summary>
public sealed class ModerationMirrorDrainer : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Named client configured with the user-management base address.</summary>
    public const string HttpClientName = "ModerationMirror";

    private readonly UserManagementModerationMirror _mirror;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ModerationMirrorDrainer> _log;

    public ModerationMirrorDrainer(
        UserManagementModerationMirror mirror,
        IHttpClientFactory http,
        ILogger<ModerationMirrorDrainer> log)
    {
        _mirror = mirror;
        _http = http;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var change in _mirror.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await PostAsync(change, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "moderation mirror POST failed for userId={UserId} suspended={IsSuspended}; " +
                    "the local projection is authoritative and the W4-05 backfill replays this state.",
                    change.UserId, change.IsSuspended);
            }
        }
    }

    private async Task PostAsync(UserModerationChange change, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        var path = change.IsSuspended
            ? $"api/User/{Uri.EscapeDataString(change.UserId)}/moderation/suspend"
            : $"api/User/{Uri.EscapeDataString(change.UserId)}/moderation/unsuspend";

        using var response = await client.PostAsJsonAsync(path, new ModerationMirrorRequest
        {
            Reason = change.IsSuspended ? change.Reason : null,
            ActorRef = change.ActorRef,
            SuspendedAt = change.IsSuspended ? change.At : null,
        }, Json, ct);

        // 404 = the identity was never projected upstream; W4-05 carries it. Not an error loop.
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound))
        {
            _log.LogWarning(
                "moderation mirror POST returned {Status} for userId={UserId}.",
                (int)response.StatusCode, change.UserId);
        }
        else if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _log.LogInformation(
                "moderation mirror: userId={UserId} unknown upstream (404); W4-05 backfill covers it.",
                change.UserId);
        }
    }
}

/// <summary>Wire shape of user-management's W4-03 suspend/unsuspend bodies.</summary>
public sealed class ModerationMirrorRequest
{
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("actorRef")] public string? ActorRef { get; init; }
    [JsonPropertyName("suspendedAt")] public DateTimeOffset? SuspendedAt { get; init; }
}
