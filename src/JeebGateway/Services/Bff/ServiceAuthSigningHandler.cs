using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC3 — produces the <c>X-Service-Auth</c> header carried
/// on every outbound BFF call. Companion to <see cref="BearerForwardingHandler"/>:
/// bearer = user identity; service-auth = caller-process identity. A
/// downstream that accepts both can decide whether a particular operation
/// requires user scope, service scope, or both.
///
/// Header format: <c>{caller}:{ts}:{nonce}:{base64-hmac}</c> where
/// <c>hmac = HMAC-SHA256(key, "{caller}|{ts}|{nonce}|{verb} {path}")</c>.
/// Including verb + path in the signed payload prevents replay against a
/// different method or resource. The unix-seconds timestamp + 16-byte nonce
/// let downstream verifiers reject replays inside the
/// <see cref="ServiceAuthOptions.ClockSkewSeconds"/> window.
///
/// The handler is configured at registration time. If
/// <see cref="ServiceAuthOptions.Enabled"/> is false it attaches an unsigned
/// caller header so downstream code paths can still observe who called them
/// in dev — production must enable it. If the signing key is missing the
/// handler throws on first use, not on startup, so dev environments that do
/// not exercise the path are not blocked.
/// </summary>
public sealed class ServiceAuthSigningHandler : DelegatingHandler
{
    public const string HeaderName = "X-Service-Auth";

    private readonly IOptions<ServiceAuthOptions> _options;
    private readonly TimeProvider _timeProvider;

    public ServiceAuthSigningHandler(
        IOptions<ServiceAuthOptions> options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;

        if (!opts.Enabled)
        {
            return base.SendAsync(request, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(opts.SigningKey))
        {
            throw new InvalidOperationException(
                "ServiceAuth is enabled but ServiceAuth:SigningKey is not configured. " +
                "Set the env var or disable ServiceAuth:Enabled for this environment.");
        }

        var ts = _timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var verb = request.Method.Method;
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var payload = $"{opts.Caller}|{ts}|{nonce}|{verb} {path}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(opts.SigningKey));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var signature = Convert.ToBase64String(mac);

        var headerValue = $"{opts.Caller}:{ts}:{nonce}:{signature}";
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, headerValue);

        return base.SendAsync(request, cancellationToken);
    }
}
