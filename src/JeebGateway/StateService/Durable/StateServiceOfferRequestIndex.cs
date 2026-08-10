using System.Text.Json;
using JeebGateway.Availability;
using JeebGateway.StateService.Idempotency;

namespace JeebGateway.StateService.Durable;

/// <summary>
/// Offer routing facts stored only in jeeb-state-service. The synchronous
/// compatibility interface is bridged with bounded owner calls; failures are
/// allowed to surface rather than being replaced with a process-local index.
/// </summary>
public sealed class StateServiceOfferRequestIndex : IOfferRequestIndex
{
    internal const string KeyPrefix = "offer-routing:";
    internal const string ReverseKeyPrefix = "offer-routing-jeeber:";
    internal const int TtlSeconds = 7 * 24 * 60 * 60;

    private readonly IExternalIdempotencyStore _owner;

    public StateServiceOfferRequestIndex(IExternalIdempotencyStore owner) => _owner = owner;

    public void Record(string offerId, string requestId) =>
        Record(offerId, requestId, jeeberId: null);

    public void Record(string offerId, string requestId, string? jeeberId)
    {
        if (string.IsNullOrWhiteSpace(offerId) || string.IsNullOrWhiteSpace(requestId)) return;
        var pairing = new Pairing(requestId, string.IsNullOrWhiteSpace(jeeberId) ? null : jeeberId);
        Put(KeyPrefix + offerId, JsonSerializer.Serialize(pairing));
        if (pairing.JeeberId is not null)
            Put(ReverseKeyPrefix + pairing.JeeberId + ":" + offerId, offerId);
    }

    public string? ResolveRequestId(string offerId) => Read(offerId)?.RequestId;

    public string? ResolveJeeberId(string offerId) => Read(offerId)?.JeeberId;

    public IReadOnlyList<string> ListOfferIdsForJeeber(string jeeberId)
    {
        if (string.IsNullOrWhiteSpace(jeeberId)) return Array.Empty<string>();
        return _owner.FindByPrefixAsync(
                ReverseKeyPrefix + jeeberId + ":", CancellationToken.None)
            .GetAwaiter().GetResult()
            .Select(row => row.ResponseBodyJson.Trim().Trim('"'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private Pairing? Read(string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId)) return null;
        var row = _owner.GetAsync(KeyPrefix + offerId, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (row is null || string.IsNullOrWhiteSpace(row.ResponseBodyJson)) return null;
        return JsonSerializer.Deserialize<Pairing>(row.ResponseBodyJson);
    }

    private void Put(string key, string body) =>
        _owner.PutOrGetAsync(key, StatusCodes.Status200OK, body, TtlSeconds, CancellationToken.None)
            .GetAwaiter().GetResult();

    private sealed record Pairing(string RequestId, string? JeeberId);
}
