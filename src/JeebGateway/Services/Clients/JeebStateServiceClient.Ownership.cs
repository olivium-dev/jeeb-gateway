#nullable enable

using System.Globalization;
using System.Net.Http.Json;
using JeebGateway.Cases;
using JeebGateway.StateService.Ownership;

namespace JeebGateway.Services.Clients;

// W1-02 — typed access to the product-neutral ownership surfaces of jeeb-state-service.
// Kept out of the NSwag-generated file so regeneration cannot erase it.
public interface IStateOwnershipClient
{
    Task<AuditEventRecordV1> AppendAuditEventAsync(
        AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct);

    Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct);

    Task<WorkItemRecordV1> CreateWorkItemAsync(
        WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct);

    Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
        string application, string kind, string subjectRef, CancellationToken ct);

    // W1-09 — the claim/complete/fail rail. These routes take no Idempotency-Key: the
    // lease token plus version CAS is the concurrency control.
    Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
        WorkClaimRequestV1 body, CancellationToken ct);

    Task<WorkItemRecordV1> CompleteWorkItemAsync(
        Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct);

    Task<WorkItemRecordV1> FailWorkItemAsync(
        Guid workItemId, WorkFailRequestV1 body, CancellationToken ct);
}

public partial class JeebStateServiceClient : IStateOwnershipClient
{
    public Task<AuditEventRecordV1> AppendAuditEventAsync(
        AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
        PostOwnershipAsync<AuditEventRecordV1>("v1/audit-events", body, idempotencyKey, ct);

    public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Application);
        var path = "v1/audit-events?application=" + Uri.EscapeDataString(query.Application)
            + "&limit=" + Math.Clamp(query.Limit, 1, 200).ToString(CultureInfo.InvariantCulture);
        Add("actorRef", query.ActorRef);
        Add("action", query.Action);
        Add("resourceType", query.ResourceType);
        Add("resourceRef", query.ResourceRef);
        Add("cursor", query.Cursor);
        return SendAsync<AuditEventPageV1>(HttpMethod.Get, path, ct);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                path += "&" + key + "=" + Uri.EscapeDataString(value);
        }
    }

    public Task<WorkItemRecordV1> CreateWorkItemAsync(
        WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct) =>
        PostOwnershipAsync<WorkItemRecordV1>("v1/work-items", body, idempotencyKey, ct);

    public async Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
        string application, string kind, string subjectRef, CancellationToken ct)
    {
        var path = "v1/work-items/latest?application=" + Uri.EscapeDataString(application)
            + "&kind=" + Uri.EscapeDataString(kind)
            + "&subjectRef=" + Uri.EscapeDataString(subjectRef);
        try
        {
            return await SendAsync<WorkItemRecordV1>(HttpMethod.Get, path, ct);
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
        WorkClaimRequestV1 body, CancellationToken ct) =>
        await PostWorkAsync<List<WorkItemRecordV1>>("v1/work-items/claim", body, ct);

    public Task<WorkItemRecordV1> CompleteWorkItemAsync(
        Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct) =>
        PostWorkAsync<WorkItemRecordV1>($"v1/work-items/{workItemId:D}/complete", body, ct);

    public Task<WorkItemRecordV1> FailWorkItemAsync(
        Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) =>
        PostWorkAsync<WorkItemRecordV1>($"v1/work-items/{workItemId:D}/fail", body, ct);

    private Task<T> PostWorkAsync<T>(string path, object body, CancellationToken ct)
        where T : class
    {
        var request = NewCaseRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, options: CaseJson);
        return SendAndDisposeAsync<T>(request, ct);
    }

    // Ownership writes carry only Idempotency-Key — the credential rides
    // StateServiceCredentialHandler, and these routes take no case actor headers.
    private Task<T> PostOwnershipAsync<T>(
        string path, object body, string idempotencyKey, CancellationToken ct)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var request = NewCaseRequest(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(body, options: CaseJson);
        return SendAndDisposeAsync<T>(request, ct);
    }
}

public sealed class UnavailableStateOwnershipClient : IStateOwnershipClient
{
    private static Task<T> Fail<T>() => Task.FromException<T>(
        new GenericCaseApiException(503, "JeebStateService:BaseUrl is not configured."));

    public Task<AuditEventRecordV1> AppendAuditEventAsync(
        AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
        Fail<AuditEventRecordV1>();

    public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
        Fail<AuditEventPageV1>();

    public Task<WorkItemRecordV1> CreateWorkItemAsync(
        WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct) =>
        Fail<WorkItemRecordV1>();

    public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
        string application, string kind, string subjectRef, CancellationToken ct) =>
        Fail<WorkItemRecordV1?>();

    public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
        WorkClaimRequestV1 body, CancellationToken ct) =>
        Fail<IReadOnlyList<WorkItemRecordV1>>();

    public Task<WorkItemRecordV1> CompleteWorkItemAsync(
        Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct) =>
        Fail<WorkItemRecordV1>();

    public Task<WorkItemRecordV1> FailWorkItemAsync(
        Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) =>
        Fail<WorkItemRecordV1>();
}
