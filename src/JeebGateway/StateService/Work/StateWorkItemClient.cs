using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.StateService.Work;

/// <summary>
/// Product-neutral client for jeeb-state-service's durable work owner. The
/// gateway supplies domain orchestration, but leases, versions, retries, and
/// terminal metadata are authoritative in state-service.
/// </summary>
public interface IStateWorkItemClient
{
    Task<StateWorkItem> CreateAsync(
        string idempotencyKey,
        StateWorkItemCreate request,
        CancellationToken ct);

    Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct);

    Task<StateWorkItem?> GetLatestAsync(
        string application,
        string kind,
        string subjectRef,
        CancellationToken ct);

    Task<IReadOnlyList<StateWorkItem>> ClaimAsync(
        StateWorkClaim request,
        CancellationToken ct);

    Task<StateWorkItem> RenewLeaseAsync(
        Guid workItemId,
        StateWorkLeaseRenew request,
        CancellationToken ct);

    Task<StateWorkItem> CompleteAsync(
        Guid workItemId,
        StateWorkComplete request,
        CancellationToken ct);

    Task<StateWorkItem> DeferAsync(
        Guid workItemId,
        StateWorkDefer request,
        CancellationToken ct);

    Task<StateWorkItem> FailAsync(
        Guid workItemId,
        StateWorkFail request,
        CancellationToken ct);

    Task<StateWorkItem> ConsumeAsync(
        Guid workItemId,
        StateWorkConsume request,
        CancellationToken ct);
}

public sealed record StateWorkItemCreate(
    string Application,
    string Kind,
    string SubjectRef,
    JsonElement? Payload,
    DateTimeOffset? DueAt,
    int? MaxAttempts,
    DateTimeOffset? RetainPayloadUntil);

public sealed record StateWorkClaim(
    string Application,
    IReadOnlyList<string>? Kinds,
    string WorkerId,
    int? LeaseSeconds,
    int? Limit);

public sealed record StateWorkLeaseRenew(Guid LeaseToken, int ExpectedVersion, int? LeaseSeconds);

public sealed record StateWorkComplete(
    Guid LeaseToken,
    int ExpectedVersion,
    JsonElement? Result,
    string? ArtifactRef,
    DateTimeOffset? ArtifactExpiresAt,
    string? DownloadTokenHash);

public sealed record StateWorkDefer(
    Guid LeaseToken,
    int ExpectedVersion,
    DateTimeOffset DueAt,
    string Reason);

public sealed record StateWorkFail(
    Guid LeaseToken,
    int ExpectedVersion,
    string Error,
    DateTimeOffset? RetryAt);

public sealed record StateWorkConsume(
    string Application,
    string DownloadTokenHash,
    int ExpectedVersion);

public sealed class StateWorkItem
{
    public Guid WorkItemId { get; init; }
    public required string Application { get; init; }
    public required string Kind { get; init; }
    public required string SubjectRef { get; init; }
    public required string Status { get; init; }
    public JsonElement Payload { get; init; }
    public JsonElement Result { get; init; }
    public string? ArtifactRef { get; init; }
    public DateTimeOffset? ArtifactExpiresAt { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public int Attempts { get; init; }
    public int MaxAttempts { get; init; }
    public int Version { get; init; }
    public Guid? LeaseToken { get; init; }
    public string? LeasedBy { get; init; }
    public DateTimeOffset? LeasedUntil { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? RetainPayloadUntil { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class StateWorkConflictException(string operation)
    : InvalidOperationException($"State work mutation '{operation}' lost its lease/version race.");

public sealed class StateWorkItemClient(HttpClient http) : IStateWorkItemClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<StateWorkItem> CreateAsync(
        string idempotencyKey,
        StateWorkItemCreate request,
        CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/work-items")
        {
            Content = JsonContent.Create(request, options: Json)
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await SendRequiredAsync(message, "create", ct);
    }

    public Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct) =>
        GetOptionalAsync($"v1/work-items/{workItemId:D}", ct);

    public Task<StateWorkItem?> GetLatestAsync(
        string application,
        string kind,
        string subjectRef,
        CancellationToken ct) => GetOptionalAsync(
        "v1/work-items/latest" +
        $"?application={Uri.EscapeDataString(application)}" +
        $"&kind={Uri.EscapeDataString(kind)}" +
        $"&subjectRef={Uri.EscapeDataString(subjectRef)}",
        ct);

    public async Task<IReadOnlyList<StateWorkItem>> ClaimAsync(
        StateWorkClaim request,
        CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/work-items/claim")
        {
            Content = JsonContent.Create(request, options: Json)
        };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "claim", ct);
        return await response.Content.ReadFromJsonAsync<List<StateWorkItem>>(Json, ct)
               ?? throw new HttpRequestException("State work claim returned an empty response body.");
    }

    public Task<StateWorkItem> RenewLeaseAsync(
        Guid workItemId,
        StateWorkLeaseRenew request,
        CancellationToken ct) => PostMutationAsync(workItemId, "lease", request, "renew", ct);

    public Task<StateWorkItem> CompleteAsync(
        Guid workItemId,
        StateWorkComplete request,
        CancellationToken ct) => PostMutationAsync(workItemId, "complete", request, "complete", ct);

    public Task<StateWorkItem> DeferAsync(
        Guid workItemId,
        StateWorkDefer request,
        CancellationToken ct) => PostMutationAsync(workItemId, "defer", request, "defer", ct);

    public Task<StateWorkItem> FailAsync(
        Guid workItemId,
        StateWorkFail request,
        CancellationToken ct) => PostMutationAsync(workItemId, "fail", request, "fail", ct);

    public Task<StateWorkItem> ConsumeAsync(
        Guid workItemId,
        StateWorkConsume request,
        CancellationToken ct) => PostMutationAsync(workItemId, "consume", request, "consume", ct);

    private async Task<StateWorkItem?> GetOptionalAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read", ct);
        return await response.Content.ReadFromJsonAsync<StateWorkItem>(Json, ct)
               ?? throw new HttpRequestException("State work read returned an empty response body.");
    }

    private async Task<StateWorkItem> PostMutationAsync<T>(
        Guid workItemId,
        string action,
        T request,
        string operation,
        CancellationToken ct)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"v1/work-items/{workItemId:D}/{action}")
        {
            Content = JsonContent.Create(request, options: Json)
        };
        return await SendRequiredAsync(message, operation, ct);
    }

    private async Task<StateWorkItem> SendRequiredAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, operation, ct);
        return await response.Content.ReadFromJsonAsync<StateWorkItem>(Json, ct)
               ?? throw new HttpRequestException($"State work {operation} returned an empty response body.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new StateWorkConflictException(operation);
        if (response.IsSuccessStatusCode)
            return;

        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"State work {operation} failed with HTTP {(int)response.StatusCode}: {detail}",
            null,
            response.StatusCode);
    }
}

/// <summary>
/// Fail-closed development/test binding used when state-service has no base URL.
/// It exists only so the stateless workflow graph remains constructible; every
/// operation fails and no local queue or metadata fallback is created.
/// Production already refuses to boot through the generic state-service
/// durability guard when this configuration is missing.
/// </summary>
public sealed class UnavailableStateWorkItemClient : IStateWorkItemClient
{
    private static HttpRequestException Error() => new(
        "JeebStateService:BaseUrl is not configured for durable work.",
        inner: null,
        HttpStatusCode.ServiceUnavailable);

    private static Task<T> Fail<T>() => Task.FromException<T>(Error());

    public Task<StateWorkItem> CreateAsync(
        string idempotencyKey,
        StateWorkItemCreate request,
        CancellationToken ct) => Fail<StateWorkItem>();

    public Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct) =>
        Fail<StateWorkItem?>();

    public Task<StateWorkItem?> GetLatestAsync(
        string application,
        string kind,
        string subjectRef,
        CancellationToken ct) => Fail<StateWorkItem?>();

    public Task<IReadOnlyList<StateWorkItem>> ClaimAsync(
        StateWorkClaim request,
        CancellationToken ct) => Fail<IReadOnlyList<StateWorkItem>>();

    public Task<StateWorkItem> RenewLeaseAsync(
        Guid workItemId,
        StateWorkLeaseRenew request,
        CancellationToken ct) => Fail<StateWorkItem>();

    public Task<StateWorkItem> CompleteAsync(
        Guid workItemId,
        StateWorkComplete request,
        CancellationToken ct) => Fail<StateWorkItem>();

    public Task<StateWorkItem> DeferAsync(
        Guid workItemId,
        StateWorkDefer request,
        CancellationToken ct) => Fail<StateWorkItem>();

    public Task<StateWorkItem> FailAsync(
        Guid workItemId,
        StateWorkFail request,
        CancellationToken ct) => Fail<StateWorkItem>();

    public Task<StateWorkItem> ConsumeAsync(
        Guid workItemId,
        StateWorkConsume request,
        CancellationToken ct) => Fail<StateWorkItem>();
}
