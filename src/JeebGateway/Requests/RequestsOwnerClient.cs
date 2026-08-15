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

    // ---- W5-04 full-record surface -------------------------------------------------

    /// <summary>Null when the owner has no such request.</summary>
    Task<RequestOwnerRow?> GetByIdAsync(string requestId, CancellationToken ct);

    /// <summary>Full-record owner list; the owner bounds it at 500 rows.</summary>
    Task<IReadOnlyList<RequestOwnerRow>> ListRecordsAsync(
        string ownerRole, string ownerId, bool oldestFirst, int limit, CancellationToken ct);

    /// <summary>Rows carrying an assignee, created at or after <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<RequestOwnerRow>> ListAssignedSinceAsync(
        DateTimeOffset since, int limit, CancellationToken ct);

    /// <summary>Per-owner tallies keyed by the owner's folded status token.</summary>
    Task<IReadOnlyDictionary<string, int>> StatusCountsAsync(
        string ownerRole, string ownerId, CancellationToken ct);

    /// <summary>Global raw-status histogram + most recently touched records.</summary>
    Task<RequestsOwnerSummary> GetSummaryAsync(int recentLimit, CancellationToken ct);

    /// <summary>True when applied; false when the row is unknown or terminal.</summary>
    Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct);

    /// <summary>True when stamped (idempotent); false when the row is unknown.</summary>
    Task<bool> SetAssigneeAsync(string requestId, string providerId, CancellationToken ct);

    /// <summary>Null when the row is unknown; otherwise committed or not-cancellable.</summary>
    Task<OwnerGuardedCancelResult?> CancelGuardedAsync(
        string requestId,
        IReadOnlyCollection<string> allowedFrom,
        string targetStatus,
        string cancelledBy,
        string? reason,
        CancellationToken ct);

    /// <summary>Null when the row is unknown OR not parked in the approval queue.</summary>
    Task<OwnerGuardedCancelResult?> DecideCancellationAsync(
        string requestId, bool approve, string fallbackStatus, CancellationToken ct);

    Task<(IReadOnlyList<RequestOwnerRow> Items, int Total)> ListPendingCancellationsAsync(
        int page, int pageSize, CancellationToken ct);

    Task<IReadOnlyList<RequestOwnerRow>> ListCancelledByAssigneeAsync(
        string providerId, string cancelledBy, CancellationToken ct);

    /// <summary>Idempotent first-flag-wins stamp. Null when unknown or terminal.</summary>
    Task<RequestOwnerRow?> MarkUnreachableAsync(string requestId, CancellationToken ct);

    Task<IReadOnlyList<RequestOwnerRow>> ListUnreachableAsync(
        DateTimeOffset cutoff, int limit, CancellationToken ct);

    /// <summary>True on the first (write-once) set; false when unknown or already set.</summary>
    Task<bool> SetEscalationRefAsync(string requestId, string escalationRef, CancellationToken ct);
}

/// <summary>Owner response for a guarded cancellation or a queue decision.</summary>
public sealed record OwnerGuardedCancelResult(
    bool Committed, string PreviousStatus, RequestOwnerRow Record);

/// <summary>Owner-wide status histogram + recent rows (the CMS dashboard read).</summary>
public sealed class RequestsOwnerSummary
{
    [JsonPropertyName("total")] public int Total { get; init; }

    [JsonPropertyName("counts")]
    public Dictionary<string, int> Counts { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("recent")]
    public List<RequestOwnerRow> Recent { get; init; } = new();
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

/// One stored request record as the owner serialises it (Go default
/// marshalling, PascalCase keys); full record since W5-04.
public sealed class RequestOwnerRow
{
    [JsonPropertyName("RequestID")] public string RequestId { get; init; } = "";
    [JsonPropertyName("ClientID")] public string ClientId { get; init; } = "";
    [JsonPropertyName("ProviderID")] public string? ProviderId { get; init; }
    [JsonPropertyName("Status")] public string Status { get; init; } = "";
    [JsonPropertyName("ConversationID")] public string? ConversationId { get; init; }
    [JsonPropertyName("CreatedAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("AcceptedAt")] public DateTimeOffset? AcceptedAt { get; init; }

    [JsonPropertyName("Title")] public string? Title { get; init; }
    [JsonPropertyName("TierID")] public string? TierId { get; init; }
    [JsonPropertyName("TierName")] public string? TierName { get; init; }
    [JsonPropertyName("PickupAddress")] public string? PickupAddress { get; init; }
    [JsonPropertyName("DropoffAddress")] public string? DropoffAddress { get; init; }
    [JsonPropertyName("OffersCount")] public int OffersCount { get; init; }
    [JsonPropertyName("Description")] public string? Description { get; init; }
    [JsonPropertyName("Transcription")] public string? Transcription { get; init; }
    [JsonPropertyName("TranscriptionConfidence")] public double? TranscriptionConfidence { get; init; }
    [JsonPropertyName("AudioURL")] public string? AudioUrl { get; init; }
    [JsonPropertyName("Photos")] public IReadOnlyList<string>? Photos { get; init; }
    [JsonPropertyName("PickupLat")] public double? PickupLat { get; init; }
    [JsonPropertyName("PickupLng")] public double? PickupLng { get; init; }
    [JsonPropertyName("DropoffLat")] public double? DropoffLat { get; init; }
    [JsonPropertyName("DropoffLng")] public double? DropoffLng { get; init; }
    [JsonPropertyName("RecipientPhone")] public string? RecipientPhone { get; init; }
    [JsonPropertyName("ScheduledAt")] public DateTimeOffset? ScheduledAt { get; init; }
    [JsonPropertyName("ActivatedAt")] public DateTimeOffset? ActivatedAt { get; init; }
    [JsonPropertyName("ExpiredAt")] public DateTimeOffset? ExpiredAt { get; init; }
    [JsonPropertyName("AcceptedFee")] public decimal? AcceptedFee { get; init; }
    [JsonPropertyName("GpsTrackingActive")] public bool GpsTrackingActive { get; init; }
    [JsonPropertyName("UpdatedAt")] public DateTimeOffset? UpdatedAt { get; init; }
    [JsonPropertyName("CancelledBy")] public string? CancelledBy { get; init; }
    [JsonPropertyName("CancellationReason")] public string? CancellationReason { get; init; }
    [JsonPropertyName("CancellationRequestedAt")] public DateTimeOffset? CancellationRequestedAt { get; init; }
    [JsonPropertyName("CancellationApprovedAt")] public DateTimeOffset? CancellationApprovedAt { get; init; }
    [JsonPropertyName("CancellationRejectedAt")] public DateTimeOffset? CancellationRejectedAt { get; init; }
    [JsonPropertyName("CancellationPreviousStatus")] public string? CancellationPreviousStatus { get; init; }
    [JsonPropertyName("UnreachableAt")] public DateTimeOffset? UnreachableAt { get; init; }
    [JsonPropertyName("EscalationRef")] public string? EscalationRef { get; init; }
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
        // BR-9 lives upstream; its 409 becomes the exception the create
        // endpoints already translate to ProblemDetails.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var cap = await response.Content.ReadFromJsonAsync<ClientAtCapBody>(Json, ct);
            if (cap?.Reason == "client_at_active_request_cap")
            {
                throw new TooManyActiveRequestsException(cap.ActiveCount, cap.Limit);
            }
        }
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

    public async Task<RequestOwnerRow?> GetByIdAsync(string requestId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Base}/{Uri.EscapeDataString(requestId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RequestOwnerRow>(Json, ct);
    }

    public async Task<IReadOnlyList<RequestOwnerRow>> ListRecordsAsync(
        string ownerRole, string ownerId, bool oldestFirst, int limit, CancellationToken ct)
    {
        var query = $"?owner_role={Uri.EscapeDataString(ownerRole)}"
            + $"&owner_id={Uri.EscapeDataString(ownerId)}"
            + $"&order={(oldestFirst ? "asc" : "desc")}&limit={limit}";
        using var response = await http.GetAsync($"{Base}/records{query}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RecordsBody>(Json, ct);
        return (IReadOnlyList<RequestOwnerRow>?)body?.Items ?? Array.Empty<RequestOwnerRow>();
    }

    public async Task<IReadOnlyList<RequestOwnerRow>> ListAssignedSinceAsync(
        DateTimeOffset since, int limit, CancellationToken ct)
    {
        var query = $"?since={Uri.EscapeDataString(since.UtcDateTime.ToString("O"))}&limit={limit}";
        using var response = await http.GetAsync($"{Base}/assigned{query}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RecordsBody>(Json, ct);
        return (IReadOnlyList<RequestOwnerRow>?)body?.Items ?? Array.Empty<RequestOwnerRow>();
    }

    public async Task<IReadOnlyDictionary<string, int>> StatusCountsAsync(
        string ownerRole, string ownerId, CancellationToken ct)
    {
        var query = $"?owner_role={Uri.EscapeDataString(ownerRole)}"
            + $"&owner_id={Uri.EscapeDataString(ownerId)}";
        using var response = await http.GetAsync($"{Base}/status-counts{query}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<StatusCountsBody>(Json, ct);
        return body?.Counts ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public async Task<RequestsOwnerSummary> GetSummaryAsync(int recentLimit, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{Base}/summary?recent_limit={recentLimit}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RequestsOwnerSummary>(Json, ct)
            ?? new RequestsOwnerSummary();
    }

    public async Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/status", new { status }, Json, ct);
        // 404 unknown and 409 terminal both read as "row untouched".
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> SetAssigneeAsync(string requestId, string providerId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/assignee",
            new { provider_id = providerId }, Json, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<OwnerGuardedCancelResult?> CancelGuardedAsync(
        string requestId,
        IReadOnlyCollection<string> allowedFrom,
        string targetStatus,
        string cancelledBy,
        string? reason,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/cancellation",
            new
            {
                allowed_from = allowedFrom,
                target_status = targetStatus,
                cancelled_by = cancelledBy,
                reason,
            }, Json, ct);
        return await ReadGuardedCancelAsync(response, ct);
    }

    public async Task<OwnerGuardedCancelResult?> DecideCancellationAsync(
        string requestId, bool approve, string fallbackStatus, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/cancellation/decision",
            new { approve, fallback_status = fallbackStatus }, Json, ct);
        return await ReadGuardedCancelAsync(response, ct);
    }

    public async Task<(IReadOnlyList<RequestOwnerRow> Items, int Total)> ListPendingCancellationsAsync(
        int page, int pageSize, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"{Base}/cancellations/pending?page={page}&page_size={pageSize}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PagedRecordsBody>(Json, ct);
        return ((IReadOnlyList<RequestOwnerRow>?)body?.Items ?? Array.Empty<RequestOwnerRow>(),
            body?.Total ?? 0);
    }

    public async Task<IReadOnlyList<RequestOwnerRow>> ListCancelledByAssigneeAsync(
        string providerId, string cancelledBy, CancellationToken ct)
    {
        var query = $"?provider_id={Uri.EscapeDataString(providerId)}"
            + $"&cancelled_by={Uri.EscapeDataString(cancelledBy)}";
        using var response = await http.GetAsync($"{Base}/cancellations/by-assignee{query}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RecordsBody>(Json, ct);
        return (IReadOnlyList<RequestOwnerRow>?)body?.Items ?? Array.Empty<RequestOwnerRow>();
    }

    public async Task<RequestOwnerRow?> MarkUnreachableAsync(string requestId, CancellationToken ct)
    {
        using var response = await http.PostAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/unreachable", content: null, ct);
        // Unknown and terminal both read as "not flaggable" (null).
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RequestOwnerRow>(Json, ct);
    }

    public async Task<IReadOnlyList<RequestOwnerRow>> ListUnreachableAsync(
        DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        var query = $"?cutoff={Uri.EscapeDataString(cutoff.UtcDateTime.ToString("O"))}&limit={limit}";
        using var response = await http.GetAsync($"{Base}/unreachable{query}", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RecordsBody>(Json, ct);
        return (IReadOnlyList<RequestOwnerRow>?)body?.Items ?? Array.Empty<RequestOwnerRow>();
    }

    public async Task<bool> SetEscalationRefAsync(
        string requestId, string escalationRef, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{Base}/{Uri.EscapeDataString(requestId)}/escalation",
            new { escalation_ref = escalationRef }, Json, ct);
        // 409 already-escalated and 404 unknown both read as "not set by this call".
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private static async Task<OwnerGuardedCancelResult?> ReadGuardedCancelAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
        {
            response.EnsureSuccessStatusCode();
        }
        var body = await response.Content.ReadFromJsonAsync<GuardedCancelBody>(Json, ct);
        if (body?.Record is null) return null;
        return new OwnerGuardedCancelResult(
            body.Outcome == "committed", body.PreviousStatus ?? "", body.Record);
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

    private sealed record RecordsBody(
        [property: JsonPropertyName("items")] List<RequestOwnerRow>? Items);

    private sealed record PagedRecordsBody(
        [property: JsonPropertyName("items")] List<RequestOwnerRow>? Items,
        [property: JsonPropertyName("total")] int Total);

    private sealed record StatusCountsBody(
        [property: JsonPropertyName("counts")] Dictionary<string, int>? Counts);

    private sealed record GuardedCancelBody(
        [property: JsonPropertyName("outcome")] string? Outcome,
        [property: JsonPropertyName("previous_status")] string? PreviousStatus,
        [property: JsonPropertyName("record")] RequestOwnerRow? Record);

    private sealed record ClientAtCapBody(
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("active_count")] int ActiveCount,
        [property: JsonPropertyName("limit")] int Limit);
}
