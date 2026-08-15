using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Requests;

/// <summary>
/// The gateway's client for delivery-service's request-owner surface
/// (<c>/api/v1/requests*</c>), which is the system of record from W5-02.
///
/// <para>Registered with resilience-only handlers plus
/// <see cref="Services.Clients.DeliveryImportCredentialHandler"/>: the standard outbound
/// pipeline forwards the CALLER's bearer, which is an end-user token and would fail a
/// service-credential check. This surface needs the service credential instead.</para>
/// </summary>
public interface IRequestsOwnerClient
{
    Task UpsertAsync(UpsertRequestUpstream request, CancellationToken ct);

    Task<AcceptRequestResult> AcceptAsync(string requestId, string providerId, CancellationToken ct);

    /// <summary>True when this call performed the transition; false when the row had
    /// already advanced (a no-op, not a failure).</summary>
    Task<bool> ExpireAsync(string requestId, CancellationToken ct);

    Task<bool> ActivateAsync(string requestId, CancellationToken ct);

    Task StampConversationAsync(string requestId, string conversationId, CancellationToken ct);

    Task<bool> SetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct);

    Task<long> AnonymizeAsync(string clientId, string pseudonym, CancellationToken ct);

    /// <summary>Null when no request carries that conversation.</summary>
    Task<RequestOwnerRow?> ByConversationAsync(string conversationId, CancellationToken ct);

    Task<IReadOnlyList<RequestOwnerRow>> DueAsync(
        string kind, DateTimeOffset cutoff, int limit, CancellationToken ct);
}

/// <summary>Outcome of an accept, mirroring the owner's four answers.</summary>
public enum AcceptRequestOutcome
{
    /// <summary>This call won the request.</summary>
    Accepted,

    /// <summary>This provider already held it — an idempotent replay, so a success.</summary>
    AlreadyMine,

    /// <summary>A different provider won first.</summary>
    TakenByAnother,

    /// <summary>The row has left the pre-acceptance set entirely.</summary>
    NotAcceptable,

    /// <summary>The owner has no such request.</summary>
    NotFound,
}

public sealed record AcceptRequestResult(AcceptRequestOutcome Outcome, string? CurrentStatus);

public sealed class RequestOwnerRow
{
    [JsonPropertyName("RequestID")] public string RequestId { get; init; } = "";
    [JsonPropertyName("ClientID")] public string ClientId { get; init; } = "";
    [JsonPropertyName("ProviderID")] public string? ProviderId { get; init; }
    [JsonPropertyName("Status")] public string Status { get; init; } = "";
    [JsonPropertyName("ConversationID")] public string? ConversationId { get; init; }
    [JsonPropertyName("CreatedAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("AcceptedAt")] public DateTimeOffset? AcceptedAt { get; init; }
}

public sealed class UpsertRequestUpstream
{
    [JsonPropertyName("request_id")] public required string RequestId { get; init; }
    [JsonPropertyName("client_id")] public required string ClientId { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("tier_id")] public string? TierId { get; init; }
    [JsonPropertyName("pickup_address")] public string? PickupAddress { get; init; }
    [JsonPropertyName("dropoff_address")] public string? DropoffAddress { get; init; }
    [JsonPropertyName("pickup_lat")] public double? PickupLat { get; init; }
    [JsonPropertyName("pickup_lng")] public double? PickupLng { get; init; }
    [JsonPropertyName("dropoff_lat")] public double? DropoffLat { get; init; }
    [JsonPropertyName("dropoff_lng")] public double? DropoffLng { get; init; }
    [JsonPropertyName("photos")] public IReadOnlyList<string>? Photos { get; init; }
    [JsonPropertyName("audio_url")] public string? AudioUrl { get; init; }
    [JsonPropertyName("transcription")] public string? Transcription { get; init; }
    [JsonPropertyName("transcription_confidence")] public double? TranscriptionConfidence { get; init; }
    [JsonPropertyName("recipient_phone")] public string? RecipientPhone { get; init; }
    [JsonPropertyName("scheduled_at")] public string? ScheduledAt { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
}

/// <inheritdoc cref="IRequestsOwnerClient"/>
public sealed class RequestsOwnerClient(HttpClient http) : IRequestsOwnerClient
{
    private const string Base = "api/v1/requests";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task UpsertAsync(UpsertRequestUpstream request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Base, request, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AcceptRequestResult> AcceptAsync(
        string requestId, string providerId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/accept",
            new { provider_id = providerId }, Json, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new AcceptRequestResult(AcceptRequestOutcome.NotFound, null);

        // 200 and 409 both carry the outcome body; only the transport errors throw.
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
            response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AcceptBody>(Json, ct);
        var outcome = body?.Outcome switch
        {
            "accepted" => AcceptRequestOutcome.Accepted,
            "already_mine" => AcceptRequestOutcome.AlreadyMine,
            "taken_by_another" => AcceptRequestOutcome.TakenByAnother,
            _ => AcceptRequestOutcome.NotAcceptable,
        };
        return new AcceptRequestResult(outcome, body?.CurrentStatus);
    }

    public Task<bool> ExpireAsync(string requestId, CancellationToken ct)
        => TransitionAsync($"{Base}/{Uri.EscapeDataString(requestId)}/expire", ct);

    public Task<bool> ActivateAsync(string requestId, CancellationToken ct)
        => TransitionAsync($"{Base}/{Uri.EscapeDataString(requestId)}/activate", ct);

    public async Task StampConversationAsync(
        string requestId, string conversationId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/conversation",
            new { conversation_id = conversationId }, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> SetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/accepted-fee",
            new { accepted_fee = fee }, Json, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.OK;
    }

    public async Task<long> AnonymizeAsync(string clientId, string pseudonym, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/anonymize", new { client_id = clientId, pseudonym }, Json, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AnonymizeBody>(Json, ct);
        return body?.Rewritten ?? 0;
    }

    public async Task<RequestOwnerRow?> ByConversationAsync(string conversationId, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"{Base}/by-conversation/{Uri.EscapeDataString(conversationId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RequestOwnerRow>(Json, ct);
    }

    public async Task<IReadOnlyList<RequestOwnerRow>> DueAsync(
        string kind, DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        var query = $"?cutoff={Uri.EscapeDataString(cutoff.UtcDateTime.ToString("O"))}&limit={limit}";
        using var response = await http.GetAsync($"{Base}/due/{kind}{query}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DueBody>(Json, ct);
        return (IReadOnlyList<RequestOwnerRow>?)body?.Items ?? Array.Empty<RequestOwnerRow>();
    }

    private async Task<bool> TransitionAsync(string path, CancellationToken ct)
    {
        using var response = await http.PostAsync(path, content: null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        // 200 applied, 204 the row had already advanced.
        return response.StatusCode == HttpStatusCode.OK;
    }

    private sealed record AcceptBody(
        [property: JsonPropertyName("outcome")] string? Outcome,
        [property: JsonPropertyName("current_status")] string? CurrentStatus);

    private sealed record AnonymizeBody(
        [property: JsonPropertyName("rewritten")] long Rewritten);

    private sealed record DueBody(
        [property: JsonPropertyName("items")] List<RequestOwnerRow>? Items);
}
