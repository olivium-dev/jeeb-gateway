using JeebGateway.Availability;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Clients;

/// <summary>
/// S06 / ADR-HB-001 AUTH CONTRACT — attaches the static
/// <c>X-Service-Auth-Key</c> header to every outbound heart-beat call
/// (<c>PATCH /v1/presence</c>, <c>GET /v1/presence/{userId}</c>).
///
/// <para>
/// <b>Why this handler exists.</b> The org-standard outbound pipeline
/// (<see cref="JeebGateway.Services.Bff.BearerForwardingHandler"/> +
/// <see cref="JeebGateway.Services.Bff.ServiceAuthSigningHandler"/>) forwards
/// the inbound mobile JWT and an HMAC <c>X-Service-Auth</c> header. heart-beat
/// (Go) authenticates EITHER a JWKS-validated end-user JWT OR a static
/// <c>X-Service-Auth-Key</c> — it does NOT verify the gateway's HMAC
/// <c>X-Service-Auth</c> scheme, and no fleet JWKS exists to validate the
/// forwarded HS256 mobile bearer. So neither standard-pipeline header
/// authenticates the gateway → heart-beat process call on its own.
/// </para>
///
/// <para>
/// The minimal, secure, additive reconciliation is to authenticate to
/// heart-beat as the trusted CALLER PROCESS via the static
/// <c>X-Service-Auth-Key</c> path heart-beat already implements
/// (constant-time compared against its <c>HEARTBEAT_SERVICE_AUTH_KEY</c>). That
/// grants heart-beat's service-auth principal, which may act on any
/// <c>userId</c> — exactly what a BFF that has already authenticated the user
/// needs. This avoids flipping the fleet-wide <c>ServiceAuth:Enabled</c> flag
/// (which would change every other downstream call) and adds no new crypto.
/// </para>
///
/// <para>
/// The handler is attached ONLY to the heart-beat typed client. When the key is
/// blank (the committed default) it is a no-op — harmless while
/// <c>FeatureFlags:Heartbeat:Enabled</c> is false, since the gateway never dials
/// heart-beat then. The forwarded mobile bearer + HMAC <c>X-Service-Auth</c>
/// from the standard pipeline remain on the request and are simply ignored by
/// heart-beat's static-key path; carrying them is benign defence-in-depth.
/// </para>
/// </summary>
public sealed class HeartBeatServiceAuthKeyHandler : DelegatingHandler
{
    /// <summary>The header heart-beat's auth middleware constant-time compares.</summary>
    public const string HeaderName = "X-Service-Auth-Key";

    private readonly IOptionsMonitor<HeartbeatFeatureOptions> _options;

    public HeartBeatServiceAuthKeyHandler(IOptionsMonitor<HeartbeatFeatureOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// heart-beat's auth middleware checks the HMAC <c>X-Service-Auth</c> header
    /// FIRST and, if it is present but signed with a key heart-beat does not
    /// share, 401s WITHOUT falling through to the static-key path. The gateway's
    /// fleet-wide <see cref="JeebGateway.Services.Bff.ServiceAuthSigningHandler"/>
    /// signs with the fleet <c>ServiceAuth:SigningKey</c>, which is NOT
    /// heart-beat's fresh <c>HEARTBEAT_SERVICE_AUTH_KEY</c>. So when we
    /// authenticate via the static key we MUST also strip any inherited HMAC
    /// header, otherwise a future fleet-wide <c>ServiceAuth:Enabled=true</c> flip
    /// would shadow the static key and 401 the heart-beat call. Stripping it is
    /// safe: heart-beat does not require the HMAC when the static key is present.
    /// </summary>
    private const string HmacHeaderName = "X-Service-Auth";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var key = _options.CurrentValue.ServiceAuthKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            // Static key is authoritative for heart-beat — remove any inherited
            // HMAC so heart-beat's middleware reaches the static-key path (see
            // remarks on HmacHeaderName), then set the canonical key.
            request.Headers.Remove(HmacHeaderName);
            request.Headers.Remove(HeaderName);
            request.Headers.Add(HeaderName, key);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
