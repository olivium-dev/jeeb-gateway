using System.Text.Json;
using JeebGateway.Services.Clients;

namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// NSwag-backed <see cref="IIdempotencyStore"/>. Persists to
/// jeeb-state-service. Because the OpenAPI documents <c>PUT /idempotency</c>
/// as <c>200</c> with no body, the generated <c>UpsertIdempotencyKeyAsync</c>
/// is void; we therefore read back the authoritative record with
/// <c>GetIdempotencyKeyAsync</c> to learn whether we inserted and to recover
/// the original body on replay. (Contract gap: see SPECS-STATUS note.)
/// </summary>
public sealed class StateServiceIdempotencyStore : IIdempotencyStore
{
    private readonly IJeebStateServiceClient _client;

    public StateServiceIdempotencyStore(IJeebStateServiceClient client) => _client = client;

    public async Task<IdempotencyOutcome> PutOrGetAsync(
        string key,
        int statusCode,
        string responseBodyJson,
        int ttlSeconds,
        CancellationToken ct)
    {
        // The body is opaque to the state-service; we send it as a JSON value
        // so it round-trips verbatim on replay.
        object? body = ParseJson(responseBodyJson);

        await _client.UpsertIdempotencyKeyAsync(
            new IdempotencyPutRequest
            {
                Key = key,
                StatusCode = statusCode,
                ResponseBody = body,
                TtlSeconds = ttlSeconds
            },
            ct);

        // Read-after-write to obtain the authoritative `inserted` flag and,
        // on replay, the ORIGINAL stored body/status (not what we just sent).
        var record = await _client.GetIdempotencyKeyAsync(key, ct);
        return ToOutcome(record);
    }

    public async Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var record = await _client.GetIdempotencyKeyAsync(key, ct);
            return ToOutcome(record);
        }
        catch (Exception ex) when (StateServiceErrors.IsNotFound(ex))
        {
            return null;
        }
    }

    private static IdempotencyOutcome ToOutcome(IdempotencyRecord record) => new()
    {
        Inserted = record.Inserted ?? false,
        StatusCode = record.StatusCode ?? 200,
        ResponseBodyJson = SerializeBody(record.ResponseBody)
    };

    private static object? ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return json; // store as a raw string if it isn't valid JSON
        }
    }

    private static string SerializeBody(object? body) => body switch
    {
        null => "null",
        JsonElement el => el.GetRawText(),
        string s => s,
        _ => JsonSerializer.Serialize(body)
    };
}
