namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed client over the real ban-service (Rust / Actix-Web, Redis-backed,
/// host port 10065, health <c>/health</c>). Hand-coded against the verified
/// routes published in <c>olivium-analysis/repos/ban-service/README.md</c> and
/// the live OpenAPI 3.1 spec at <c>/api-docs/openapi.json</c> — see
/// <see cref="JeebGateway.Services.Clients.BanServiceClient"/> for the rationale
/// for hand-coding over NSwag (snake_case + OpenAPI-3.1 nullable arrays).
///
/// ban-service models a PROGRESSIVE ban (WARNING → PARTIAL_BAN → BAN) whose
/// durations are owned by the service's <c>banning-rule.json</c>, not by the
/// caller. The gateway's <see cref="BanServiceJeeberRestrictionStore"/> wraps
/// this client to satisfy the narrower
/// <see cref="JeebGateway.Requests.Cancellation.IJeeberRestrictionStore"/>
/// contract (is-restricted / expiry / apply-restriction).
/// </summary>
public interface IBanServiceClient
{
    /// <summary>
    /// <c>GET /api/v1/ban/{userId}/status</c> — returns every active and
    /// historical ban-status row for the user (one per ban_type). The gateway
    /// derives "currently restricted" from any row whose
    /// <see cref="BanStatusItem.IsCurrentlyBanned"/> is true, and the active
    /// expiry from the maximum non-null <see cref="BanStatusItem.BannedUntil"/>.
    /// </summary>
    Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct);

    /// <summary>
    /// <c>POST /api/v1/ban/{userId}/{banType}</c> — advances the user one stage
    /// in the given ban progression and returns the resulting status. The
    /// gateway uses this to record the abuse-control restriction trigger; the
    /// resulting <see cref="BanStatusItem.BannedUntil"/> (when the stage is a
    /// PARTIAL_BAN) is ban-service-authoritative.
    /// </summary>
    Task<BanStatusItem> ApplyBanAsync(string userId, string banType, CancellationToken ct);
}

/// <summary>
/// Gateway-side projection of ban-service's <c>BanStatusesResponse</c>
/// (<c>{ "user_id":..., "ban_statuses":[BanStatusResponse] }</c>).
/// </summary>
public sealed class BanStatusesResult
{
    public string UserId { get; init; } = string.Empty;
    public IReadOnlyList<BanStatusItem> BanStatuses { get; init; } = Array.Empty<BanStatusItem>();

    /// <summary>True when any ban row reports the user as currently banned.</summary>
    public bool IsCurrentlyBanned => BanStatuses.Any(s => s.IsCurrentlyBanned);

    /// <summary>
    /// The latest expiry across all currently-active partial bans, or null
    /// when there is no active time-boxed ban (permanent bans report
    /// <see cref="BanStatusItem.BannedUntil"/> = null even while banned).
    /// </summary>
    public DateTimeOffset? ActiveExpiry =>
        BanStatuses
            .Where(s => s.IsCurrentlyBanned && s.BannedUntil is not null)
            .Select(s => s.BannedUntil!.Value)
            .DefaultIfEmpty()
            .Max() is { } max && max != default
            ? max
            : null;
}

/// <summary>
/// Gateway-side projection of ban-service's <c>BanStatusResponse</c>.
/// </summary>
public sealed class BanStatusItem
{
    public string UserId { get; init; } = string.Empty;
    public string BanType { get; init; } = string.Empty;
    public int CurrentStage { get; init; }

    /// <summary>One of <c>WARNING</c>, <c>PARTIAL_BAN</c>, <c>BAN</c>.</summary>
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>Expiry for a time-boxed PARTIAL_BAN; null for WARNING or permanent BAN.</summary>
    public DateTimeOffset? BannedUntil { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    public bool IsCurrentlyBanned { get; init; }
}
