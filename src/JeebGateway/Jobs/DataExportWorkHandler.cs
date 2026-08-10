using System.Text.Json;
using JeebGateway.Artifacts;
using JeebGateway.Infrastructure;
using JeebGateway.StateService.Work;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Options;

namespace JeebGateway.Jobs;

public sealed class DataExportWorkHandler(
    IDataExportPackager packager,
    IDataExportNotifier notifier,
    IPrivateArtifactStore artifacts,
    IDataExportTokenProtector tokens,
    IOptions<DataExportOptions> options,
    TimeProvider clock) : IDurableWorkItemHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => DurableWorkContract.DataExportKind;

    public async Task<DurableWorkExecutionResult> ExecuteAsync(
        StateWorkItem item,
        CancellationToken ct)
    {
        DataExportWorkPayload? request;
        try
        {
            request = item.Payload.ValueKind == JsonValueKind.Object
                ? item.Payload.Deserialize<DataExportWorkPayload>(Json)
                : null;
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request is null
            || string.IsNullOrWhiteSpace(request.UserId)
            || !DataExportFormat.All.Contains(request.Format))
            return DurableWorkExecutionResult.Failed("data-export payload is invalid");

        var idempotencyKey = $"data-export:{item.WorkItemId:N}";
        var metadata = DataExportPackageMetadata.For(
            request.UserId,
            request.Format,
            item.CreatedAt);

        // The artifact owner uses strict request-hash idempotency. A previous
        // upload can have committed even when the gateway timed out before
        // receiving its response, so recover that immutable response before
        // rereading mutable profile/order/rating/chat sources.
        var stored = await artifacts.RecoverUploadAsync(idempotencyKey, ct);
        if (stored is not null && stored.ExpiresAt <= clock.GetUtcNow())
            return DurableWorkExecutionResult.Failed(
                "recovered data-export artifact expired before workflow completion");

        if (stored is null)
        {
            var now = clock.GetUtcNow();
            var packagingDeadline = item.CreatedAt + options.Value.Sla;
            if (now > packagingDeadline)
                return DurableWorkExecutionResult.Failed(
                    "data-export SLA expired before packaging completed");

            DataExportPayload payload;
            try
            {
                payload = await packager.BuildAsync(
                    request.UserId,
                    request.Format,
                    item.CreatedAt,
                    ct);
            }
            catch (OwnerCapabilityUnavailableException ex)
            {
                // An incomplete right-of-access package is worse than a late
                // package: never upload or notify until every owner can supply
                // its section. Keep the durable item explicit and retryable,
                // then fail visibly if the contractual SLA is exhausted.
                var retryAt = now + PositiveOrDefault(
                    options.Value.SourceUnavailableRetryDelay,
                    TimeSpan.FromMinutes(15));
                return retryAt < packagingDeadline
                    ? DurableWorkExecutionResult.Deferred(ex.Message, retryAt)
                    : DurableWorkExecutionResult.Failed(
                        $"{ex.Message} The data-export SLA expired before a complete package could be built.");
            }
            // Expiry remains the documented validity window after the package
            // is ready. A retry never has to reproduce this wall-clock value:
            // it recovers a completed owner response before rebuilding, while
            // an in-progress race is retried until that response is visible.
            var requestedExpiry = clock.GetUtcNow() + options.Value.LinkValidity;
            stored = await artifacts.PutAsync(
                idempotencyKey,
                item.SubjectRef,
                metadata.FileName,
                metadata.ContentType,
                payload.Bytes,
                requestedExpiry,
                ct);
        }
        var capability = tokens.Create(item.WorkItemId);

        // Notification is keyed by the stable work id/token and must be
        // idempotent in its owner. It intentionally precedes the terminal CAS:
        // a crash can then retry the same notification/artifact, while completing
        // first would make a crash permanently lose the ready notification.
        await notifier.NotifyReadyAsync(
            request.UserId,
            item.WorkItemId.ToString("D"),
            capability.Token,
            stored.ExpiresAt,
            ct);

        var result = JsonSerializer.SerializeToElement(new
        {
            contentType = metadata.ContentType,
            fileName = metadata.FileName,
            sizeBytes = stored.SizeBytes
        });
        return DurableWorkExecutionResult.Completed(
            result,
            stored.ArtifactRef,
            stored.ExpiresAt,
            capability.TokenHash);
    }

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}
