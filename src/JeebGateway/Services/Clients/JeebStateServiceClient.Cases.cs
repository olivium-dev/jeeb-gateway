#nullable enable

using System.Net.Http.Json;
using JeebGateway.Cases;

namespace JeebGateway.Services.Clients;

public interface IGenericCaseStateClient
{
    Task<GenericCaseV1> CreateCaseAsync(CreateGenericCaseRequestV1 body, string idempotencyKey,
        string actorRef, string actorRole, CancellationToken ct);
    Task<GenericCaseV1> GetCaseAsync(Guid caseId, CancellationToken ct);
    Task<GenericCasePageV1> ListCasesAsync(GenericCaseQueryV1 query, CancellationToken ct);
    Task<GenericCaseV1> PatchCaseAsync(Guid caseId, PatchGenericCaseRequestV1 body,
        string idempotencyKey, string actorRef, string actorRole, CancellationToken ct);
    Task<GenericCaseStatusMessageV1> ApplyCaseStatusMessageAsync(Guid caseId,
        ApplyGenericCaseStatusMessageRequestV1 body, string idempotencyKey,
        string actorRef, string actorRole, CancellationToken ct) =>
        Task.FromException<GenericCaseStatusMessageV1>(
            new NotSupportedException("Atomic case status messages are not implemented by this client."));
    Task<GenericCaseMessageCreatedV1> AddCaseMessageAsync(Guid caseId,
        CreateGenericCaseMessageRequestV1 body, string idempotencyKey,
        string actorRef, string actorRole, CancellationToken ct);
    Task<IReadOnlyList<GenericCaseMessageV1>> GetCaseMessagesAsync(Guid caseId,
        bool includeInternal, CancellationToken ct);
    async Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(Guid caseId,
        bool includeInternal, int limit, string? cursor, CancellationToken ct)
    {
        var messages = await GetCaseMessagesAsync(caseId, includeInternal, ct);
        var window = CaseCursorPagination.Messages(messages, cursor, limit);
        return new GenericCaseMessagePageV1 { Items = window.Items, NextCursor = window.NextCursor };
    }
    async Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(Guid caseId,
        bool includeInternal, string order, int limit, string? cursor, CancellationToken ct)
    {
        if (order == GenericCaseMessageOrders.Newest)
            return await GetCaseMessagesPageAsync(caseId, includeInternal, limit, cursor, ct);
        if (order != GenericCaseMessageOrders.Oldest || cursor is not null)
            throw new ArgumentException("Unsupported message page order or cursor.", nameof(order));
        var messages = await GetCaseMessagesAsync(caseId, includeInternal, ct);
        return new GenericCaseMessagePageV1
        {
            Items = messages.OrderBy(item => item.CaseVersion).ThenBy(item => item.MessageId)
                .Take(Math.Clamp(limit, 1, 200)).ToArray(),
        };
    }
    Task<IReadOnlyList<GenericCaseAttachmentV1>> GetCaseAttachmentsAsync(Guid caseId, CancellationToken ct);
    Task<IReadOnlyList<GenericCaseAuditEventV1>> GetCaseAuditAsync(Guid caseId, CancellationToken ct);
    Task<GenericCaseDeadLetterPageV1> GetCaseDeadLettersAsync(
        int limit, string? cursor, CancellationToken ct);
    Task<GenericCaseDeadLetterRequeueV1> RequeueCaseDeadLetterAsync(
        Guid eventId, string idempotencyKey, string actorRef, CancellationToken ct);
}

public partial interface IJeebStateServiceClient;

// Kept separate from the generated state client so regeneration cannot erase
// the canonical generic-case surface.
public partial class JeebStateServiceClient : IGenericCaseStateClient
{
    private static readonly System.Text.Json.JsonSerializerOptions CaseJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public Task<GenericCaseV1> CreateCaseAsync(CreateGenericCaseRequestV1 body,
        string idempotencyKey, string actorRef, string actorRole, CancellationToken ct) =>
        SendMutationAsync<GenericCaseV1>(HttpMethod.Post, "v1/cases", body,
            idempotencyKey, actorRef, actorRole, ct);

    public Task<GenericCaseV1> GetCaseAsync(Guid caseId, CancellationToken ct) =>
        SendAsync<GenericCaseV1>(HttpMethod.Get, $"v1/cases/{caseId:D}", ct);

    public Task<GenericCaseV1> PatchCaseAsync(Guid caseId, PatchGenericCaseRequestV1 body,
        string idempotencyKey, string actorRef, string actorRole, CancellationToken ct) =>
        SendMutationAsync<GenericCaseV1>(HttpMethod.Patch, $"v1/cases/{caseId:D}", body,
            idempotencyKey, actorRef, actorRole, ct);

    public Task<GenericCaseStatusMessageV1> ApplyCaseStatusMessageAsync(Guid caseId,
        ApplyGenericCaseStatusMessageRequestV1 body, string idempotencyKey,
        string actorRef, string actorRole, CancellationToken ct) =>
        SendMutationAsync<GenericCaseStatusMessageV1>(HttpMethod.Post,
            $"v1/cases/{caseId:D}/status-message", body,
            idempotencyKey, actorRef, actorRole, ct);

    public Task<GenericCaseMessageCreatedV1> AddCaseMessageAsync(Guid caseId,
        CreateGenericCaseMessageRequestV1 body, string idempotencyKey,
        string actorRef, string actorRole, CancellationToken ct) =>
        SendMutationAsync<GenericCaseMessageCreatedV1>(HttpMethod.Post,
            $"v1/cases/{caseId:D}/messages", body, idempotencyKey, actorRef, actorRole, ct);

    public async Task<IReadOnlyList<GenericCaseMessageV1>> GetCaseMessagesAsync(Guid caseId,
        bool includeInternal, CancellationToken ct) =>
        (await GetCaseMessagesPageAsync(caseId, includeInternal, 200, null, ct)).Items;

    public Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(Guid caseId,
        bool includeInternal, int limit, string? cursor, CancellationToken ct)
        => GetCaseMessagesPageAsync(caseId, includeInternal, GenericCaseMessageOrders.Newest,
            limit, cursor, ct);

    public Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(Guid caseId,
        bool includeInternal, string order, int limit, string? cursor, CancellationToken ct)
    {
        if (order is not (GenericCaseMessageOrders.Newest or GenericCaseMessageOrders.Oldest))
            throw new ArgumentOutOfRangeException(nameof(order));
        var path = $"v1/cases/{caseId:D}/messages"
            + $"?includeInternal={includeInternal.ToString().ToLowerInvariant()}"
            + $"&order={order}"
            + $"&limit={Math.Clamp(limit, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(cursor))
            path += "&cursor=" + Uri.EscapeDataString(cursor);
        return SendAsync<GenericCaseMessagePageV1>(HttpMethod.Get, path, ct);
    }

    public Task<IReadOnlyList<GenericCaseAttachmentV1>> GetCaseAttachmentsAsync(
        Guid caseId, CancellationToken ct) => SendAsync<IReadOnlyList<GenericCaseAttachmentV1>>(
            HttpMethod.Get, $"v1/cases/{caseId:D}/attachments", ct);

    public Task<IReadOnlyList<GenericCaseAuditEventV1>> GetCaseAuditAsync(
        Guid caseId, CancellationToken ct) => SendAsync<IReadOnlyList<GenericCaseAuditEventV1>>(
            HttpMethod.Get, $"v1/cases/{caseId:D}/audit", ct);

    public Task<GenericCaseDeadLetterPageV1> GetCaseDeadLettersAsync(
        int limit, string? cursor, CancellationToken ct)
    {
        var path = $"v1/case-outbox/dead-letters?limit={Math.Clamp(limit, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);
        var request = NewCaseRequest(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Actor-Role", "admin");
        return SendAndDisposeAsync<GenericCaseDeadLetterPageV1>(request, ct);
    }

    public Task<GenericCaseDeadLetterRequeueV1> RequeueCaseDeadLetterAsync(
        Guid eventId, string idempotencyKey, string actorRef, CancellationToken ct) =>
        SendMutationAsync<GenericCaseDeadLetterRequeueV1>(HttpMethod.Post,
            $"v1/case-outbox/dead-letters/{eventId:D}/requeue", new { },
            idempotencyKey, actorRef, "admin", ct);

    public Task<GenericCasePageV1> ListCasesAsync(GenericCaseQueryV1 query, CancellationToken ct)
    {
        var values = new List<KeyValuePair<string, string>>();
        Add("query", query.Query);
        Add("kind", query.Kind);
        Add("status", query.Status);
        Add("priority", query.Priority);
        Add("assigneeRef", query.AssigneeRef);
        Add("assigned", query.Assigned?.ToString().ToLowerInvariant());
        Add("requesterRef", query.RequesterRef);
        Add("participantRef", query.ParticipantRef);
        Add("subjectType", query.SubjectType);
        Add("subjectRef", query.SubjectRef);
        Add("dueBefore", query.DueBefore?.ToUniversalTime().ToString("o"));
        Add("active", query.Active?.ToString().ToLowerInvariant());
        Add("sort", query.Sort);
        Add("limit", Math.Clamp(query.Limit, 1, 200).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("cursor", query.Cursor);
        var suffix = string.Join("&", values.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return SendAsync<GenericCasePageV1>(HttpMethod.Get, "v1/cases?" + suffix, ct);

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values.Add(new(key, value));
        }
    }

    private Task<T> SendMutationAsync<T>(HttpMethod method, string path, object body,
        string idempotencyKey, string actorRef, string actorRole, CancellationToken ct)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorRole);
        var request = NewCaseRequest(method, path);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("X-Actor-Ref", actorRef);
        request.Headers.TryAddWithoutValidation("X-Actor-Role", actorRole);
        request.Content = JsonContent.Create(body, options: CaseJson);
        return SendAndDisposeAsync<T>(request, ct);
    }

    private Task<T> SendAsync<T>(HttpMethod method, string path, CancellationToken ct)
        where T : class => SendAndDisposeAsync<T>(NewCaseRequest(method, path), ct);

    private HttpRequestMessage NewCaseRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_baseUrl), relativePath));
        request.Headers.Accept.Add(
            System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));
        return request;
    }

    private async Task<T> SendAndDisposeAsync<T>(HttpRequestMessage request, CancellationToken ct)
        where T : class
    {
        using (request)
        {
            PrepareRequest(_httpClient, request, request.RequestUri!.ToString());
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            ProcessResponse(_httpClient, response);
            if (!response.IsSuccessStatusCode)
            {
                var error = response.Content is null ? null : await response.Content.ReadAsStringAsync(ct);
                throw new GenericCaseApiException((int)response.StatusCode, error);
            }
            var result = await response.Content.ReadFromJsonAsync<T>(CaseJson, ct);
            return result ?? throw new GenericCaseApiException(StatusCodes.Status502BadGateway,
                $"State service returned an empty body for {request.RequestUri}.");
        }
    }
}

public sealed class UnavailableGenericCaseStateClient : IGenericCaseStateClient
{
    private static GenericCaseApiException Error() => new(503, "JeebStateService:BaseUrl is not configured.");
    private static Task<T> Fail<T>() => Task.FromException<T>(Error());

    public Task<GenericCaseV1> CreateCaseAsync(CreateGenericCaseRequestV1 body, string key,
        string actorRef, string actorRole, CancellationToken ct) => Fail<GenericCaseV1>();
    public Task<GenericCaseV1> GetCaseAsync(Guid caseId, CancellationToken ct) => Fail<GenericCaseV1>();
    public Task<GenericCasePageV1> ListCasesAsync(GenericCaseQueryV1 query, CancellationToken ct) =>
        Fail<GenericCasePageV1>();
    public Task<GenericCaseV1> PatchCaseAsync(Guid caseId, PatchGenericCaseRequestV1 body, string key,
        string actorRef, string actorRole, CancellationToken ct) => Fail<GenericCaseV1>();
    public Task<GenericCaseMessageCreatedV1> AddCaseMessageAsync(Guid caseId,
        CreateGenericCaseMessageRequestV1 body, string key, string actorRef, string actorRole,
        CancellationToken ct) => Fail<GenericCaseMessageCreatedV1>();
    public Task<IReadOnlyList<GenericCaseMessageV1>> GetCaseMessagesAsync(Guid caseId,
        bool includeInternal, CancellationToken ct) => Fail<IReadOnlyList<GenericCaseMessageV1>>();
    public Task<IReadOnlyList<GenericCaseAttachmentV1>> GetCaseAttachmentsAsync(Guid caseId,
        CancellationToken ct) => Fail<IReadOnlyList<GenericCaseAttachmentV1>>();
    public Task<IReadOnlyList<GenericCaseAuditEventV1>> GetCaseAuditAsync(Guid caseId,
        CancellationToken ct) => Fail<IReadOnlyList<GenericCaseAuditEventV1>>();
    public Task<GenericCaseDeadLetterPageV1> GetCaseDeadLettersAsync(
        int limit, string? cursor, CancellationToken ct) => Fail<GenericCaseDeadLetterPageV1>();
    public Task<GenericCaseDeadLetterRequeueV1> RequeueCaseDeadLetterAsync(
        Guid eventId, string key, string actorRef, CancellationToken ct) =>
        Fail<GenericCaseDeadLetterRequeueV1>();
}
