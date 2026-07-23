using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner.JeeberSearch;

/// <summary>
/// Typed user-management client for PP-3 free-text jeeber discovery: the read a partner performs to
/// find a top-up destination by name/phone BEFORE resolving its wallet-target (<see
/// cref="JeebGateway.Controllers.PartnerJeebersController"/>). The gateway never touches a DB — this
/// is a thin BFF seam over the user-management microservice (ADR-0001), mirroring the hand-authored
/// <see cref="JeebGateway.Users.HttpUserManagementDualRoleClient"/> precedent (same
/// <c>UserManagementServiceApi:BaseUrl</c> base address, bearer-forwarded + Polly resilience).
///
/// <para><b>Why hand-written, not NSwag.</b> The committed user-management OpenAPI
/// (<c>contracts/user-management.openapi.json</c>) exposes no free-text search operation — its only
/// listing endpoint (<c>GET /api/User/all</c>) has neither a query parameter nor a phone field — so
/// there is nothing for NSwag to generate. This follows the established repo precedent for upstream
/// capabilities NSwag cannot model (OfferServiceClient, BanServiceClient, UpgSettlementClient). The
/// UM-side search capability (<c>GET api/User/jeebers/search</c> returning jeeber-type users with a
/// phone) is the documented upstream dependency for this BFF; the seam is wired + offline-proven and
/// goes live the moment user-management exposes it.</para>
/// </summary>
public interface IPartnerJeeberSearchClient
{
    /// <summary>
    /// Free-text search over jeeber-type users' name/phone. Returns the RAW (unmasked) hits; the BFF
    /// controller masks the phone to the last four digits before returning them to the caller.
    /// Throws <see cref="PartnerJeeberSearchUpstreamException"/> on any user-management failure.
    /// </summary>
    Task<IReadOnlyList<PartnerJeeberSearchHit>> SearchJeebersAsync(string query, int limit, CancellationToken ct);
}

/// <summary>
/// A raw (unmasked) jeeber search hit as returned by user-management. <see cref="Phone"/> is the full
/// number; the BFF masks it (keep-last-4) before it ever leaves the gateway.
/// </summary>
public sealed record PartnerJeeberSearchHit(string JeeberId, string DisplayName, string? Phone);

/// <summary>
/// An upstream (user-management) failure while searching jeebers. The controller maps this to a
/// sanitized RFC 7807 <c>502 Bad Gateway</c> ProblemDetails (the upstream status is logged
/// server-side only, never echoed to the caller). Kept DISTINCT from
/// <see cref="PartnerWalletException"/> so an upstream-down fault is never mislabeled a 409.
/// </summary>
public sealed class PartnerJeeberSearchUpstreamException : Exception
{
    /// <summary>The upstream HTTP status (or 502 for a transport/parse fault). Logged, never echoed.</summary>
    public int UpstreamStatusCode { get; }

    public PartnerJeeberSearchUpstreamException(int upstreamStatusCode, Exception? inner = null)
        : base($"user-management jeeber search failed (upstream status {upstreamStatusCode}).", inner)
        => UpstreamStatusCode = upstreamStatusCode;
}
