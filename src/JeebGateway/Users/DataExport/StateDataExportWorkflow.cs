using System.Text.Json;
using JeebGateway.Artifacts;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

public interface IDataExportWorkflow
{
    Task<DataExportRequest> RequestAsync(string userId, string format, CancellationToken ct);
    Task<DataExportRequest?> GetLatestForUserAsync(string userId, CancellationToken ct);
    Task<Uri?> RedeemDownloadAsync(string token, CancellationToken ct);
}

/// <summary>
/// Stateless data-export facade. Metadata, leases, and terminal capability
/// hashes live in state-service; bytes live only in the private artifact owner.
/// </summary>
public sealed class StateDataExportWorkflow(
    IStateWorkItemClient work,
    IPrivateArtifactStore artifacts,
    IDataExportTokenProtector tokens,
    IOptions<DataExportOptions> options,
    TimeProvider clock) : IDataExportWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DataExportRequest> RequestAsync(
        string userId,
        string format,
        CancellationToken ct)
    {
        var subject = DurableWorkContract.SubjectForUser(userId);
        var latest = await work.GetLatestAsync(
            DurableWorkContract.Application,
            DurableWorkContract.DataExportKind,
            subject,
            ct);
        if (latest is not null && IsOpen(latest, clock.GetUtcNow()))
            return Map(latest, userId);

        var predecessor = latest is null ? "initial" : $"after:{latest.WorkItemId:N}";
        var idempotencyKey = $"data-export:{subject}:{predecessor}";
        var payload = JsonSerializer.SerializeToElement(
            new DataExportWorkPayload(userId, format), Json);
        try
        {
            var created = await work.CreateAsync(
                idempotencyKey,
                new StateWorkItemCreate(
                    DurableWorkContract.Application,
                    DurableWorkContract.DataExportKind,
                    subject,
                    payload,
                    DueAt: null,
                    MaxAttempts: 10,
                    RetainPayloadUntil: null),
                ct);
            return Map(created, userId);
        }
        catch (StateWorkConflictException)
        {
            // A concurrent caller can observe the same terminal predecessor and
            // race this create with different requested formats. The owner
            // accepts one idempotency payload; the loser returns that winning
            // open request rather than creating a duplicate.
            latest = await work.GetLatestAsync(
                DurableWorkContract.Application,
                DurableWorkContract.DataExportKind,
                subject,
                ct);
            if (latest is not null && IsOpen(latest, clock.GetUtcNow()))
                return Map(latest, userId);
            throw;
        }
    }

    public async Task<DataExportRequest?> GetLatestForUserAsync(
        string userId,
        CancellationToken ct)
    {
        var latest = await work.GetLatestAsync(
            DurableWorkContract.Application,
            DurableWorkContract.DataExportKind,
            DurableWorkContract.SubjectForUser(userId),
            ct);
        return latest is null ? null : Map(latest, userId);
    }

    public async Task<Uri?> RedeemDownloadAsync(string token, CancellationToken ct)
    {
        if (!tokens.TryValidate(token, out var capability))
            return null;

        var item = await work.GetAsync(capability.WorkItemId, ct);
        var now = clock.GetUtcNow();
        if (item is null
            || !string.Equals(item.Application, DurableWorkContract.Application, StringComparison.Ordinal)
            || !string.Equals(item.Kind, DurableWorkContract.DataExportKind, StringComparison.Ordinal)
            || !string.Equals(item.Status, "completed", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(item.ArtifactRef)
            || item.ArtifactExpiresAt is not { } expiresAt
            || expiresAt <= now)
            return null;

        var validity = expiresAt - now;
        var signed = await artifacts.CreateDownloadUrlAsync(
            item.ArtifactRef,
            validity > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : validity,
            singleUse: true,
            ct);

        try
        {
            await work.ConsumeAsync(
                item.WorkItemId,
                new StateWorkConsume(
                    DurableWorkContract.Application,
                    capability.TokenHash,
                    item.Version),
                ct);
            return signed.Url;
        }
        catch (StateWorkConflictException)
        {
            // Another concurrent redemption won the atomic consume. Discard the
            // pre-minted private URL and preserve the external 404 contract.
            return null;
        }
    }

    private DataExportRequest Map(StateWorkItem item, string fallbackUserId)
    {
        var payload = DeserializePayload(item);
        var now = clock.GetUtcNow();
        var status = item.Status switch
        {
            "queued" => DataExportStatus.Queued,
            "leased" => DataExportStatus.Processing,
            "completed" when item.ArtifactExpiresAt <= now => DataExportStatus.Expired,
            "completed" => DataExportStatus.Ready,
            "consumed" => DataExportStatus.Delivered,
            "failed" or "cancelled" => DataExportStatus.Failed,
            _ => DataExportStatus.Failed
        };
        var format = DataExportFormat.All.Contains(payload?.Format ?? string.Empty)
            ? payload!.Format
            : DataExportFormat.Json;
        var token = status == DataExportStatus.Ready
            ? tokens.Create(item.WorkItemId)
            : null;

        return new DataExportRequest
        {
            Id = item.WorkItemId.ToString("D"),
            UserId = payload?.UserId ?? fallbackUserId,
            Status = status,
            Format = format,
            RequestedAt = item.CreatedAt,
            DueBy = item.CreatedAt + options.Value.Sla,
            StartedAt = item.Attempts > 0 ? item.UpdatedAt : null,
            ReadyAt = item.Status is "completed" or "consumed" ? item.CompletedAt : null,
            DeliveredAt = item.Status == "consumed" ? item.UpdatedAt : null,
            FailedAt = item.Status is "failed" or "cancelled" ? item.CompletedAt : null,
            FailureReason = item.LastError,
            DownloadToken = token?.Token,
            LinkExpiresAt = item.ArtifactExpiresAt,
            Payload = null,
            PayloadContentType = StringResult(item.Result, "contentType"),
            PayloadSizeBytes = LongResult(item.Result, "sizeBytes")
        };
    }

    private static bool IsOpen(StateWorkItem item, DateTimeOffset now) =>
        item.Status is "queued" or "leased"
        || (item.Status == "completed" && item.ArtifactExpiresAt > now);

    private static DataExportWorkPayload? DeserializePayload(StateWorkItem item)
    {
        try
        {
            return item.Payload.ValueKind == JsonValueKind.Object
                ? item.Payload.Deserialize<DataExportWorkPayload>(Json)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringResult(JsonElement result, string property) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? LongResult(JsonElement result, string property) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty(property, out var value)
        && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

}

public sealed record DataExportWorkPayload(string UserId, string Format);
