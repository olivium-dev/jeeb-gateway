#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Cases;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Notifications;

/// <summary>
/// W1-11 — the channel drain persists each job as a state-service work item BEFORE it fans
/// out, so a crash mid-drain leaves a durable record instead of nothing.
/// </summary>
public interface INewRequestFanoutWorkItems
{
    /// <summary>Never throws. Returns false when nothing durable was written.</summary>
    Task<bool> PersistAsync(NewRequestNotification job, CancellationToken ct);
}

/// <inheritdoc />
public sealed class StateServiceNewRequestFanoutWorkItems : INewRequestFanoutWorkItems
{
    public const string Application = "jeeb-gateway";
    public const string Kind = "new-request-fanout";

    private readonly IStateOwnershipClient _client;
    private readonly NewRequestFanoutOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<StateServiceNewRequestFanoutWorkItems> _log;

    public StateServiceNewRequestFanoutWorkItems(
        IStateOwnershipClient client,
        IOptions<NewRequestFanoutOptions> options,
        TimeProvider clock,
        ILogger<StateServiceNewRequestFanoutWorkItems> log)
    {
        _client = client;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// G-15 — the idempotency key is <c>new-request-fanout:&lt;requestId&gt;</c>: one work item
    /// per request, so a redrive replays the same row instead of minting a duplicate job.
    /// </summary>
    public static string IdempotencyKeyFor(string requestId) => Kind + ":" + requestId;

    public async Task<bool> PersistAsync(NewRequestNotification job, CancellationToken ct)
    {
        if (job is null || string.IsNullOrWhiteSpace(job.RequestId))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        try
        {
            await _client.CreateWorkItemAsync(
                new WorkItemCreateRequestV1
                {
                    Application = Application,
                    Kind = Kind,
                    SubjectRef = job.RequestId,
                    Payload = JsonSerializer.SerializeToElement(job),
                    DueAt = now,
                    RetainPayloadUntil = now + _options.RetainPayloadTtl,
                },
                IdempotencyKeyFor(job.RequestId),
                ct);
            return true;
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == StatusCodes.Status503ServiceUnavailable)
        {
            // State-service is not wired in this environment; the direct drain still runs.
            _log.LogDebug(ex, "newreq-fanout work item skipped for {RequestId}", job.RequestId);
            return false;
        }
        catch (Exception ex)
        {
            // DEGRADE-DON'T-FAIL: an unreachable work-item rail must never cost a live push.
            _log.LogWarning(ex,
                "newreq-fanout work item persist failed for {RequestId}; fanning out unpersisted",
                job.RequestId);
            return false;
        }
    }
}

/// <summary>
/// W1-11 pre-send status probe. A fan-out drained late (after a restart, or behind a slow
/// queue) must never push a request whose auction already closed.
/// </summary>
public interface INewRequestFanoutStatusProbe
{
    /// <summary>
    /// Cheap single-row status read. Returns null when the status cannot be resolved —
    /// the caller then FAILS OPEN and still fans out.
    /// </summary>
    Task<string?> ReadStatusAsync(string requestId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class RequestsStoreFanoutStatusProbe : INewRequestFanoutStatusProbe
{
    private readonly IRequestsStore _requests;
    private readonly ILogger<RequestsStoreFanoutStatusProbe> _log;

    public RequestsStoreFanoutStatusProbe(
        IRequestsStore requests, ILogger<RequestsStoreFanoutStatusProbe> log)
    {
        _requests = requests;
        _log = log;
    }

    public async Task<string?> ReadStatusAsync(string requestId, CancellationToken ct)
    {
        try
        {
            var row = await _requests.GetAsync(requestId, ct);
            return row?.Status;
        }
        catch (Exception ex)
        {
            // Unresolvable status is fail-open by contract: never drop a push on a store blip.
            _log.LogDebug(ex, "newreq-fanout status probe failed for {RequestId}", requestId);
            return null;
        }
    }
}

/// <summary>Null object: every probe is unresolvable, so the fan-out always proceeds.</summary>
public sealed class AlwaysOpenFanoutStatusProbe : INewRequestFanoutStatusProbe
{
    public static readonly AlwaysOpenFanoutStatusProbe Instance = new();

    public Task<string?> ReadStatusAsync(string requestId, CancellationToken ct)
        => Task.FromResult<string?>(null);
}
