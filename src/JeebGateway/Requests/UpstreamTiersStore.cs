using System.Text.RegularExpressions;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests;

/// <summary>
/// iter6 B2 (tier-contract): tier-existence probe that reconciles the
/// request-create tier namespace with the catalog <c>GET /v1/tiers</c> hands
/// to the mobile picker.
///
/// <para><b>Why this exists.</b> <see cref="InMemoryTiersStore"/> only accepts
/// the five legacy lowercase slug codes (<c>flash/express/standard/...</c>).
/// When <c>FeatureFlags:UseUpstream:Delivery</c> is on, <c>GET /v1/tiers</c> is
/// served from delivery-service and returns tiers keyed by a <b>UUID id</b>
/// plus a <c>name</c> field — NOT the slug. Mobile #64 now sends that UUID as
/// <c>tierId</c> on <c>POST /requests</c>, so the slug-only allowlist rejected
/// every create with "tierId does not match any active delivery tier". This
/// adapter unifies the namespace: a tier id is valid when it matches a live
/// catalog tier by <b>UUID id</b>, by <b>name</b> (case-insensitive), or by a
/// slugified name (e.g. "On-the-Way" → "on-the-way"). The legacy slug codes
/// remain accepted as a fallback so nothing that worked before regresses.</para>
///
/// <para>Selected over <see cref="InMemoryTiersStore"/> in <c>Program.cs</c>.
/// When the Delivery upstream flag is OFF (tests, local default) it short-
/// circuits to the legacy slug allowlist and never touches the network, so the
/// existing fixtures stay green. If the upstream catalog read throws, it falls
/// back to the slug allowlist rather than failing the create closed — a
/// transient delivery-service hiccup must not block a slug-based request.</para>
///
/// Mirrors the upstream-adapter pattern used by
/// <see cref="JeebGateway.Availability.UpstreamPendingOffersStore"/> (flag-
/// gated, in-memory fallback retained).
/// </summary>
public sealed class UpstreamTiersStore : ITiersStore
{
    private readonly IDeliveryServiceClient _delivery;
    private readonly UpstreamFeatureFlags _flags;
    private readonly InMemoryTiersStore _fallback;
    private readonly ILogger<UpstreamTiersStore> _logger;

    public UpstreamTiersStore(
        IDeliveryServiceClient delivery,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<UpstreamTiersStore> logger)
    {
        _delivery = delivery;
        _flags = flags.Value;
        _fallback = new InMemoryTiersStore();
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string tierCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierCode))
        {
            return false;
        }

        var candidate = tierCode.Trim();

        // Legacy slug codes (flash/standard/...) are always honored so the
        // pre-existing slug-based create path and its fixtures never regress.
        if (await _fallback.ExistsAsync(candidate, ct))
        {
            return true;
        }

        // When the live catalog is NOT the upstream source, the slug allowlist
        // is the whole truth — nothing else to consult.
        if (!_flags.Delivery)
        {
            return false;
        }

        // Reconcile against the live tier catalog (the same list GET /v1/tiers
        // serves the mobile picker): accept the UUID id, the name, or a
        // slugified name.
        try
        {
            var tiers = await _delivery.ListTiersAsync(ct);
            foreach (var t in tiers)
            {
                if (string.Equals(t.Id, candidate, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Slugify(t.Name), candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            // Fail-soft: a transient catalog read failure must not block a
            // create whose tierId is a known slug (already handled above). For
            // a UUID we cannot verify here, so deny — but log loudly.
            _logger.LogWarning(ex,
                "iter6-B2: live tier-catalog lookup failed while validating tierId {TierId}; "
                + "denying (slug allowlist already checked).",
                candidate);
            return false;
        }
    }

    private static string Slugify(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        return Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
    }
}
