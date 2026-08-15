using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.StateService.Audit;

/// <summary>
/// Product-neutral client for jeeb-state-service's append-only administrator
/// audit stream. Domain-specific naming and authorization remain in the gateway;
/// state-service owns only immutable event durability and bounded queries.
/// </summary>
public interface IStateAuditClient
{
    Task<StateAuditEvent> AppendAsync(
        string idempotencyKey,
        StateAuditAppend request,
        CancellationToken ct);

    Task<StateAuditPage> FindAsync(
        StateAuditQuery query,
        CancellationToken ct);
}

public sealed record StateAuditAppend(
    string Application,
    string ActorRef,
    string ActorRole,
    string Action,
    string ResourceType,
    string ResourceRef,
    string? RequestId,
    JsonElement? Before,
    JsonElement? After,
    JsonElement? Metadata,
    DateTimeOffset? OccurredAt);

public sealed record StateAuditQuery(
    string Application,
    string? ActorRef,
    string? Action,
    string? ResourceType,
    string? ResourceRef,
    int Limit,
    string? Cursor);

public sealed class StateAuditEvent
{
    public Guid EventId { get; init; }
    public required string Application { get; init; }
    public required string ActorRef { get; init; }
    public required string ActorRole { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceRef { get; init; }
    public string? RequestId { get; init; }
    public JsonElement Before { get; init; }
    public JsonElement After { get; init; }
    public JsonElement Metadata { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class StateAuditPage
{
    public IReadOnlyList<StateAuditEvent> Items { get; init; } = Array.Empty<StateAuditEvent>();
    public string? NextCursor { get; init; }
}

public sealed class StateAuditClient(HttpClient http) : IStateAuditClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<StateAuditEvent> AppendAsync(
        string idempotencyKey,
        StateAuditAppend request,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audit-events")
        {
            Content = JsonContent.Create(request, options: Json)
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "append", ct);
        return await response.Content.ReadFromJsonAsync<StateAuditEvent>(Json, ct)
               ?? throw new HttpRequestException("State audit append returned an empty response body.");
    }

    public async Task<StateAuditPage> FindAsync(StateAuditQuery query, CancellationToken ct)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("application", query.Application),
            new("limit", Math.Clamp(query.Limit, 1, 200).ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        Add("actorRef", query.ActorRef);
        Add("action", query.Action);
        Add("resourceType", query.ResourceType);
        Add("resourceRef", query.ResourceRef);
        Add("cursor", query.Cursor);
        var suffix = string.Join("&", values.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var response = await http.GetAsync(
            "v1/audit-events?" + suffix,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        await EnsureSuccessAsync(response, "find", ct);
        return await response.Content.ReadFromJsonAsync<StateAuditPage>(Json, ct)
               ?? throw new HttpRequestException("State audit query returned an empty response body.");

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values.Add(new(key, value));
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"State audit {operation} failed with HTTP {(int)response.StatusCode}: {detail}",
            null,
            response.StatusCode);
    }
}

public sealed class UnavailableStateAuditClient : IStateAuditClient
{
    private static HttpRequestException Error() => new(
        "JeebStateService:BaseUrl is not configured for administrator audit events.",
        inner: null,
        HttpStatusCode.ServiceUnavailable);

    private static Task<T> Fail<T>() => Task.FromException<T>(Error());

    public Task<StateAuditEvent> AppendAsync(
        string idempotencyKey,
        StateAuditAppend request,
        CancellationToken ct) => Fail<StateAuditEvent>();

    public Task<StateAuditPage> FindAsync(
        StateAuditQuery query,
        CancellationToken ct) => Fail<StateAuditPage>();
}
