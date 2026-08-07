using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Cases;

namespace JeebGateway.Admin;

public sealed record AdminDeliveryPartyIds(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("courier_id")] string CourierId);

public sealed record AdminDeliveryAllowedOperation(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("target_status")] string TargetStatus,
    [property: JsonPropertyName("expected_status")] string ExpectedStatus);

public sealed record AdminDeliveryResource(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("party_ids")] AdminDeliveryPartyIds PartyIds,
    [property: JsonPropertyName("current_status")] string CurrentStatus,
    [property: JsonPropertyName("tier_id")] string? TierId,
    [property: JsonPropertyName("pickup_lat")] decimal? PickupLat,
    [property: JsonPropertyName("pickup_lng")] decimal? PickupLng,
    [property: JsonPropertyName("evidence_url")] string? EvidenceUrl,
    [property: JsonPropertyName("allowed_operations")] IReadOnlyList<AdminDeliveryAllowedOperation> AllowedOperations,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record AdminDeliverySummary(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("party_ids")] AdminDeliveryPartyIds PartyIds,
    [property: JsonPropertyName("current_status")] string CurrentStatus,
    [property: JsonPropertyName("tier_id")] string? TierId,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record AdminDeliveryListResponse(
    [property: JsonPropertyName("deliveries")] IReadOnlyList<AdminDeliverySummary> Deliveries,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record AdminDeliveryTimelineEntry(
    [property: JsonPropertyName("transition_id")] string TransitionId,
    [property: JsonPropertyName("from_status")] string FromStatus,
    [property: JsonPropertyName("to_status")] string ToStatus,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("actor_id")] string ActorId,
    [property: JsonPropertyName("evidence_url")] string? EvidenceUrl,
    [property: JsonPropertyName("geo")] JsonElement? Geo,
    [property: JsonPropertyName("transitioned_at")] DateTimeOffset TransitionedAt);

public sealed record AdminDeliveryTimelineResponse(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("party_ids")] AdminDeliveryPartyIds PartyIds,
    [property: JsonPropertyName("current_status")] string CurrentStatus,
    [property: JsonPropertyName("timeline")] IReadOnlyList<AdminDeliveryTimelineEntry> Timeline,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record AdminSourceHealth(string Status, string? Reason);
public sealed record AdminDeliveryFieldAvailability(string Status, string Source);
public sealed record AdminDeliveryEvidenceAggregate(
    JsonElement? Proof,
    JsonElement? Route,
    JsonElement? LatestCourierLocation,
    CasePageResponseV2? RelatedCases,
    JsonElement? CodSettlements,
    JsonElement? Chat);
public sealed record AdminDeliveryAggregateData(
    AdminDeliveryResource Delivery,
    AdminDeliveryTimelineResponse? Timeline,
    AdminDeliveryEvidenceAggregate Evidence,
    IReadOnlyDictionary<string, AdminSourceHealth> SourceHealth,
    IReadOnlyDictionary<string, AdminDeliveryFieldAvailability> FieldAvailability,
    bool Partial);
public sealed record AdminDeliveryAggregateResponse(AdminDeliveryAggregateData Data);

public sealed record AdminDeliveryTransitionRequest(
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("expected_status")] string? ExpectedStatus,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record AdminDeliveryTransitionResponse(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("previous_status")] string PreviousStatus,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("transition_id")] string TransitionId,
    [property: JsonPropertyName("transitioned_at")] DateTimeOffset TransitionedAt);

public sealed record AdminSettlementRecipient(string Type, string Id);
public sealed record AdminSettlementReconciliation(
    string? BatchId, string? BatchStatus, int? Version, string? PaymentReference,
    string? Note, string? PaidBy, IReadOnlyList<string> AllowedActions);
public sealed record AdminSettlementHistoryEvent(
    string Type, DateTimeOffset At, string? ActorId, JsonElement Details);
public sealed record AdminSettlementResource(
    string Id, string DeliveryId, string ProviderId, string? Payer,
    AdminSettlementRecipient Recipient, string GrossAmount, string CommissionAmount,
    string NetAmount, string Currency, string Status, int Version, long? SnapshotSequence,
    DateTimeOffset? PaidAt, DateTimeOffset? BatchedAt, string? DisputeReason,
    DateTimeOffset? DisputedAt, string? DisputedBy, string? ResolutionNote,
    DateTimeOffset? ResolvedAt, string? ResolvedBy, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? BatchId, AdminSettlementReconciliation? Reconciliation,
    IReadOnlyList<AdminSettlementHistoryEvent>? History);
public sealed record AdminSettlementPageCursor(string? NextCursor);
public sealed record AdminSettlementPageResponse(
    IReadOnlyList<AdminSettlementResource> Data, AdminSettlementPageCursor Page);
public sealed record AdminSettlementDetailResponse(AdminSettlementResource Data);

public sealed record AdminSettlementBatchResource(
    string Id,
    [property: JsonPropertyName("jeeber_id")] string ProviderId,
    [property: JsonPropertyName("total_net_usd")] string TotalNetUsd,
    string Currency, string Status,
    [property: JsonPropertyName("period_start")] DateOnly PeriodStart,
    [property: JsonPropertyName("period_end")] DateOnly PeriodEnd,
    [property: JsonPropertyName("settlement_count")] int SettlementCount,
    [property: JsonPropertyName("paid_at")] DateTimeOffset? PaidAt,
    [property: JsonPropertyName("paid_by")] string? PaidBy,
    int Version,
    [property: JsonPropertyName("payment_reference")] string? PaymentReference,
    [property: JsonPropertyName("reconciliation_note")] string? ReconciliationNote,
    JsonElement? Metadata,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    IReadOnlyList<AdminSettlementResource>? Settlements);
public sealed record AdminSettlementBatchResponse(AdminSettlementBatchResource Data);
public sealed record AdminSettlementReconcileResponse(
    AdminSettlementBatchResource Data,
    [property: JsonPropertyName("settlements_updated")] int SettlementsUpdated,
    [property: JsonPropertyName("notification_id")] string NotificationId);
