#nullable enable

using System.Net.Http.Json;
using JeebGateway.Cases;
using JeebGateway.StateService.Config;

namespace JeebGateway.Services.Clients;

// W3-03 — typed access to the unified versioned-config primitive (G-27). Same partial-class
// shape as W1-02's ownership surfaces so NSwag regeneration cannot erase it.
public interface IStateConfigClient
{
    Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
        string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct);

    Task<ConfigVersionRecordV1> PublishAsync(
        string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct);

    Task<ConfigSurfaceRecordV1?> GetSurfaceAsync(
        string application, string surfaceKey, CancellationToken ct);

    Task<ConfigAckRecordV1> UpsertAckAsync(
        string subjectRef, string surfaceKey, ConfigAckUpsertRequestV1 body, CancellationToken ct);

    Task<ConfigAckRecordV1?> GetAckAsync(
        string application, string subjectRef, string surfaceKey, CancellationToken ct);
}

public partial class JeebStateServiceClient : IStateConfigClient
{
    public Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
        string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct) =>
        SendConfigAsync<ConfigSurfaceRecordV1>(
            HttpMethod.Put, $"config-surfaces/{Esc(surfaceKey)}/draft", body, null, ct);

    // G-15 — the publish leg is the only mutation that mints a version, so it carries the key.
    public Task<ConfigVersionRecordV1> PublishAsync(
        string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return SendConfigAsync<ConfigVersionRecordV1>(
            HttpMethod.Post, $"config-surfaces/{Esc(surfaceKey)}/publish", body, idempotencyKey, ct);
    }

    public Task<ConfigSurfaceRecordV1?> GetSurfaceAsync(
        string application, string surfaceKey, CancellationToken ct) =>
        GetOrNullAsync<ConfigSurfaceRecordV1>(
            $"config-surfaces/{Esc(surfaceKey)}?application={Esc(application)}", ct);

    public Task<ConfigAckRecordV1> UpsertAckAsync(
        string subjectRef, string surfaceKey, ConfigAckUpsertRequestV1 body, CancellationToken ct) =>
        SendConfigAsync<ConfigAckRecordV1>(
            HttpMethod.Put, $"acks/{Esc(subjectRef)}/{Esc(surfaceKey)}", body, null, ct);

    public Task<ConfigAckRecordV1?> GetAckAsync(
        string application, string subjectRef, string surfaceKey, CancellationToken ct) =>
        GetOrNullAsync<ConfigAckRecordV1>(
            $"acks/{Esc(subjectRef)}/{Esc(surfaceKey)}?application={Esc(application)}", ct);

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        try
        {
            return await SendAsync<T>(HttpMethod.Get, path, ct);
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    private Task<T> SendConfigAsync<T>(
        HttpMethod method, string path, object body, string? idempotencyKey, CancellationToken ct)
        where T : class
    {
        var request = NewCaseRequest(method, path);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }
        request.Content = JsonContent.Create(body, options: CaseJson);
        return SendAndDisposeAsync<T>(request, ct);
    }
}

public sealed class UnavailableStateConfigClient : IStateConfigClient
{
    private static Task<T> Fail<T>() => Task.FromException<T>(
        new GenericCaseApiException(503, "JeebStateService:BaseUrl is not configured."));

    public Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
        string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct) =>
        Fail<ConfigSurfaceRecordV1>();

    public Task<ConfigVersionRecordV1> PublishAsync(
        string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct) =>
        Fail<ConfigVersionRecordV1>();

    public Task<ConfigSurfaceRecordV1?> GetSurfaceAsync(
        string application, string surfaceKey, CancellationToken ct) =>
        Fail<ConfigSurfaceRecordV1?>();

    public Task<ConfigAckRecordV1> UpsertAckAsync(
        string subjectRef, string surfaceKey, ConfigAckUpsertRequestV1 body, CancellationToken ct) =>
        Fail<ConfigAckRecordV1>();

    public Task<ConfigAckRecordV1?> GetAckAsync(
        string application, string subjectRef, string surfaceKey, CancellationToken ct) =>
        Fail<ConfigAckRecordV1?>();
}
