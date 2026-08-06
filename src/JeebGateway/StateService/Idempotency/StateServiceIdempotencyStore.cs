using System.Text.Json;
using JeebGateway.Services.Clients;

namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// NSwag-backed <see cref="IIdempotencyStore"/>. Persists to jeeb-state-service.
///
/// <para>JEBV4-344 — the <c>inserted</c> flag comes from the PUT's OWN response and
/// nowhere else. The previous implementation threw the PUT response away (the OpenAPI
/// document types the operation <c>void</c>) and re-read the key with
/// <c>GetIdempotencyKeyAsync</c>. A read is never an insert, so the read-back always
/// answered <c>inserted:false</c> and every reserve-before-act caller took its
/// "already exists" branch forever. Verified at the wire against the live service:</para>
/// <code>
/// PUT  /v1/state/idempotency       -> 201 {"inserted": true}    (first)
/// PUT  /v1/state/idempotency       -> 200 {"inserted": false}   (replay, original body)
/// GET  /v1/state/idempotency/{key} -> 200 {"inserted": false}   (always)
/// </code>
/// <para>The PUT response also carries the ORIGINAL stored status + body on replay, so
/// the read-back is redundant in the normal path. It is retained only as a degradation
/// path for a 2xx that carries no parseable body.</para>
/// </summary>
public sealed class StateServiceIdempotencyStore : IExternalIdempotencyStore
{
    private readonly IJeebStateServiceClient _client;
    private readonly ILogger<StateServiceIdempotencyStore> _log;

    public StateServiceIdempotencyStore(
        IJeebStateServiceClient client,
        ILogger<StateServiceIdempotencyStore> log)
    {
        _client = client;
        _log = log;
    }

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

        var upsert = await _client.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest
            {
                Key = key,
                StatusCode = statusCode,
                ResponseBody = body,
                TtlSeconds = ttlSeconds
            },
            ct);

        var inserted = upsert.ResolveInserted();

        // Normal path: the PUT answered with the authoritative record — it already holds
        // both the insert flag AND the originally-stored status/body. No read-back needed.
        if (upsert.Record is not null && inserted is not null)
        {
            return ToOutcome(upsert.Record, inserted.Value);
        }

        // Degradation path: a 2xx with no parseable body. Recover the stored status/body
        // with the read-back, but NEVER take its `inserted` — that field is false by
        // construction on a GET. Where the PUT's status settled the question (201/200) we
        // keep that answer; a bodyless 204 leaves it genuinely unknown, and unknown must
        // fail towards "not inserted" so a reserve-before-act caller does not act twice.
        _log.LogWarning(
            "state-service PUT /v1/state/idempotency returned {Status} with no usable record "
            + "for key {Key}; falling back to read-back (inserted={Inserted})",
            upsert.HttpStatusCode, key, inserted);

        var record = await _client.GetIdempotencyKeyAsync(key, ct);
        return ToOutcome(record, inserted ?? false);
    }

    public async Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var record = await _client.GetIdempotencyKeyAsync(key, ct);
            // A read is never an insert — Inserted is false here by definition, not by
            // accident. Callers of GetAsync only consume existence + body.
            return ToOutcome(record, inserted: false);
        }
        catch (Exception ex) when (StateServiceErrors.IsNotFound(ex))
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct)
    {
        var records = await _client.FindIdempotencyKeysByPrefixAsync(prefix, ct);
        return records.Select(r => ToOutcome(r, inserted: false)).ToList();
    }

    private static IdempotencyOutcome ToOutcome(IdempotencyRecord record, bool inserted) => new()
    {
        Inserted = inserted,
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
