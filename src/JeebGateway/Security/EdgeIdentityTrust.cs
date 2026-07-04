using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// SEC-C1 (Leg-11 hardening) — the single decision point for whether the gateway may
/// trust client-supplied <c>X-User-Id</c> / <c>X-User-Roles</c> identity headers on a
/// given request.
///
/// <para><b>Why this exists.</b> The gateway historically accepted these headers as an
/// "edge-injected" identity for an admin/edge path. In production the box has NO reverse
/// proxy stripping them and the public edge (Cloudflare) forwards custom headers by
/// default, so a raw client could send <c>X-User-Id</c>/<c>X-User-Roles</c> and be treated
/// as any user (identity spoof) with any role (admin escalation). Identity must derive
/// ONLY from a verified JWT, or from an internal-only header that is proven to come from a
/// trusted edge via a shared secret.</para>
///
/// <para><b>The gate.</b>
/// <list type="number">
///   <item>In <c>Development</c> / <c>Testing</c> the header identity is trusted — the local
///         dev shell and the integration-test harness drive identity via <c>X-User-Id</c>
///         and never mint a bearer. These environments are never the public host.</item>
///   <item>In any other environment (Production and friends) the headers are trusted ONLY
///         when a non-empty <c>Security:EdgeIdentity:SharedSecret</c> is configured AND the
///         request presents the matching value in the configured header
///         (constant-time compared). With no secret configured — the committed default —
///         the headers are IGNORED (fail closed): identity comes from the JWT alone.</item>
/// </list>
/// </para>
///
/// The committed configuration ships NO secret, so live deploys fail closed with no config
/// change required; a real trusted edge is re-enabled purely by injecting
/// <c>Security__EdgeIdentity__SharedSecret</c> from a swarm secret.
/// </summary>
internal static class EdgeIdentityTrust
{
    /// <summary>
    /// True when inbound <c>X-User-*</c> identity headers may be trusted for this request.
    /// </summary>
    public static bool HeadersTrusted(HttpContext? httpContext)
    {
        if (httpContext?.RequestServices is null)
        {
            return false;
        }

        var env = httpContext.RequestServices.GetService<IHostEnvironment>();
        if (env is not null
            && (env.IsDevelopment()
                || string.Equals(env.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var edge = httpContext.RequestServices
            .GetService<IOptions<SecurityOptions>>()?.Value?.EdgeIdentity;

        if (edge is null || string.IsNullOrEmpty(edge.SharedSecret))
        {
            // Fail closed: no trusted-edge secret configured → never trust raw client headers.
            return false;
        }

        if (httpContext.Request.Headers.TryGetValue(edge.SharedSecretHeader, out var presented)
            && !string.IsNullOrEmpty(presented))
        {
            var presentedBytes = Encoding.UTF8.GetBytes(presented.ToString());
            var secretBytes = Encoding.UTF8.GetBytes(edge.SharedSecret);
            return CryptographicOperations.FixedTimeEquals(presentedBytes, secretBytes);
        }

        return false;
    }
}
