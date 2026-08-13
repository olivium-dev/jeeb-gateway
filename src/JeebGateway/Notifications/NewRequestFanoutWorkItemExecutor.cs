#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Services;
using JeebGateway.StateService.Ownership;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JeebGateway.Notifications;

/// <summary>
/// W1-12 claim side of the W1-11 fan-out work item: fans out a job the worker holds a lease
/// for, through the SAME notifier the inline drain uses (no transport change).
/// </summary>
public sealed class NewRequestFanoutWorkItemExecutor : IWorkItemExecutor
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _scope;
    private readonly bool _enabled;

    // The notifier graph is resolved LAZILY: the worker constructs every executor just to read
    // Enabled, and a gated-off kind must never drag a client graph into the boot path.
    public NewRequestFanoutWorkItemExecutor(
        IServiceProvider scope,
        IOptions<UpstreamFeatureFlags> flags)
    {
        _scope = scope;
        _enabled = flags.Value.FanoutWorkQueue;
    }

    public string Kind => StateServiceNewRequestFanoutWorkItems.Kind;

    // FeatureFlags:UseUpstream:FanoutWorkQueue defaults false, so this is false as shipped;
    // the flip is W1-12.
    public bool Enabled => _enabled;

    public async Task ExecuteAsync(WorkItemRecordV1 item, CancellationToken ct)
    {
        if (item.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"new-request-fanout work item {item.WorkItemId:D} carries no payload.");
        }

        var job = item.Payload.Deserialize<NewRequestNotification>(PayloadJson)
            ?? throw new InvalidOperationException(
                $"new-request-fanout work item {item.WorkItemId:D} has an unreadable payload.");

        await _scope.GetRequiredService<INewRequestPushNotifier>().FanOutAsync(job, ct);
    }
}
