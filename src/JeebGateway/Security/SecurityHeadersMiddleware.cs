using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// Applies OWASP API Security Top-10 baseline response headers
/// (T-backend-032 AC: HSTS, X-Content-Type, etc.). Runs after CORS so the
/// browser-evaluated Access-Control-* set is not stripped.
///
/// Header rationale (OWASP API Security 2023 — API8 Security Misconfiguration):
///   - HSTS                       force HTTPS, prevent SSL strip
///   - X-Content-Type-Options     block MIME-sniff
///   - X-Frame-Options            legacy clickjacking guard (CSP frame-ancestors above is the real one)
///   - Referrer-Policy            never leak full URL on cross-origin nav
///   - Permissions-Policy         disable powerful browser features (API tier)
///   - Cross-Origin-*-Policy      Spectre isolation hardening
///   - Content-Security-Policy    defence-in-depth; default-src 'none' for JSON APIs
///   - X-Powered-By / Server      strip — no fingerprinting hints
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<SecurityOptions> _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptionsMonitor<SecurityOptions> options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var opts = _options.CurrentValue.Headers;
        if (!opts.Enabled)
        {
            return _next(context);
        }

        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;

            // HSTS — only meaningful over HTTPS, but emitting on HTTP is
            // harmless and lets reverse proxies (Traefik / Cloudflare) inherit.
            var hsts = $"max-age={opts.HstsMaxAgeSeconds}";
            if (opts.HstsIncludeSubdomains) hsts += "; includeSubDomains";
            if (opts.HstsPreload) hsts += "; preload";
            h["Strict-Transport-Security"] = hsts;

            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] =
                "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-site";
            h["Cross-Origin-Embedder-Policy"] = "require-corp";

            if (!string.IsNullOrWhiteSpace(opts.ContentSecurityPolicy))
            {
                h["Content-Security-Policy"] = opts.ContentSecurityPolicy;
            }

            // Fingerprint reduction.
            h.Remove("Server");
            h.Remove("X-Powered-By");

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
