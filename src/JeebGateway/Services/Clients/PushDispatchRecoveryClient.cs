using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

public interface IPushDispatchRecoveryClient
{
    Task<PushDispatchListV1> ListStaleAsync(int olderThanSeconds, int limit, CancellationToken ct);
    Task<PushDispatchStatusV1> GetAsync(string idempotencyKey, int staleAfterSeconds, CancellationToken ct);
    Task<PushDispatchStatusV1> ResolveAsync(
        string idempotencyKey, PushDispatchResolutionV1 request, CancellationToken ct);
}

public sealed class PushDispatchRecoveryClient : IPushDispatchRecoveryClient
{
    public const string HttpClientName = "ServicePushNotificationClient";
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _clients;

    public PushDispatchRecoveryClient(IHttpClientFactory clients) => _clients = clients;

    public Task<PushDispatchListV1> ListStaleAsync(
        int olderThanSeconds, int limit, CancellationToken ct) => SendAsync<PushDispatchListV1>(
        HttpMethod.Get,
        $"api/v1/sent-payload/idempotency/stale?older_than_seconds={olderThanSeconds}&limit={limit}",
        null, ct);

    public Task<PushDispatchStatusV1> GetAsync(
        string idempotencyKey, int staleAfterSeconds, CancellationToken ct) => SendAsync<PushDispatchStatusV1>(
        HttpMethod.Get,
        $"api/v1/sent-payload/idempotency/{Uri.EscapeDataString(idempotencyKey)}"
        + $"?stale_after_seconds={staleAfterSeconds}",
        null, ct);

    public Task<PushDispatchStatusV1> ResolveAsync(
        string idempotencyKey, PushDispatchResolutionV1 request, CancellationToken ct) =>
        SendAsync<PushDispatchStatusV1>(HttpMethod.Post,
            $"api/v1/sent-payload/idempotency/{Uri.EscapeDataString(idempotencyKey)}/resolve",
            request, ct);

    private async Task<T> SendAsync<T>(
        HttpMethod method, string relativePath, object? body, CancellationToken ct)
    {
        var client = _clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? detail = null;
            try
            {
                using var problem = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (problem.RootElement.TryGetProperty("detail", out var value))
                    detail = value.GetString();
            }
            catch (JsonException)
            {
                // Upstream status remains authoritative when a proxy returns non-JSON.
            }
            throw new PushDispatchRecoveryApiException((int)response.StatusCode, detail);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new PushDispatchRecoveryApiException(StatusCodes.Status502BadGateway);
    }
}

public sealed class PushDispatchRecoveryApiException(int statusCode, string? detail = null)
    : Exception($"push-notification recovery call failed with {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
    public string? Detail { get; } = detail;
}

public sealed class PushDispatchListV1
{
    public IReadOnlyList<PushDispatchStatusV1> Items { get; init; } = Array.Empty<PushDispatchStatusV1>();
    public int Count { get; init; }
}

public sealed class PushDispatchStatusV1
{
    [JsonPropertyName("idempotency_key")]
    public required string IdempotencyKey { get; init; }

    [JsonPropertyName("target_user_id")]
    public required string TargetUserId { get; init; }

    public required string State { get; init; }

    public int Version { get; init; }

    [JsonPropertyName("http_status")]
    public int? HttpStatus { get; init; }

    [JsonPropertyName("response_message")]
    public string? ResponseMessage { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("last_error_at")]
    public DateTimeOffset? LastErrorAt { get; init; }

    [JsonPropertyName("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; init; }

    [JsonPropertyName("resolution_note")]
    public string? ResolutionNote { get; init; }

    public bool Stale { get; init; }
}

public sealed class PushDispatchResolutionV1
{
    public required string Outcome { get; init; }
    public required string Note { get; init; }

    [JsonPropertyName("response_message")]
    public string? ResponseMessage { get; init; }

    [JsonPropertyName("observed_version")]
    public int ObservedVersion { get; init; }

    [JsonPropertyName("observed_updated_at")]
    public DateTimeOffset ObservedUpdatedAt { get; init; }
}
