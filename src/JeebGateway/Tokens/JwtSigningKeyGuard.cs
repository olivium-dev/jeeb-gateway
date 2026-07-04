using System.Text;
using Microsoft.Extensions.Hosting;

namespace JeebGateway.Tokens;

/// <summary>
/// SEC-H2 (Leg-11 hardening) — fail-closed boot guard on the JWT signing key.
///
/// <para>The committed <c>appsettings.json</c> ships a placeholder signing key
/// (<c>REPLACE-WITH-PRODUCTION-SIGNING-KEY-32+</c>) and the code default is a dev key
/// (<c>dev-only-signing-key-32-bytes-minimum!!</c>). Both are ≥32 bytes, so the existing
/// length check in <see cref="TokenService"/> passes and a deploy that forgot to inject the
/// real secret would boot with a publicly-known key — anyone could then forge a valid
/// gateway token for any user.</para>
///
/// <para>This guard refuses to start the app in any non-Development/Testing environment when
/// the effective signing key is empty, too short, or a known placeholder/dev default. It
/// bakes NO key value — it only asserts that a real one was supplied from configuration.
/// Development and Testing keep the dev default so local runs and the integration-test
/// harness are unaffected.</para>
/// </summary>
internal static class JwtSigningKeyGuard
{
    /// <summary>
    /// Signing keys that must never be accepted in production. These are the values committed
    /// to the repo / baked as code defaults; presence of any of them means the real secret
    /// was not injected.
    /// </summary>
    internal static readonly string[] KnownPlaceholders =
    {
        "REPLACE-WITH-PRODUCTION-SIGNING-KEY-32+",
        "dev-only-signing-key-32-bytes-minimum!!",
    };

    /// <summary>
    /// Case-insensitive markers that strongly indicate a non-secret placeholder even if the
    /// exact string drifts (e.g. someone edits the placeholder). Defence in depth.
    /// </summary>
    private static readonly string[] PlaceholderMarkers =
    {
        "REPLACE-WITH", "CHANGE-ME", "CHANGEME", "PLACEHOLDER", "DEV-ONLY", "EXAMPLE-KEY",
    };

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> (refuse to start) when <paramref name="signingKey"/>
    /// is a placeholder/dev/too-short key in a non-Development/Testing environment.
    /// No-op in Development / Testing.
    /// </summary>
    /// <param name="signingKey">The effective key that will sign/validate tokens.</param>
    /// <param name="environment">The host environment.</param>
    /// <param name="keyName">Config key name, for a precise error message.</param>
    public static void EnsureNotPlaceholder(string? signingKey, IHostEnvironment environment, string keyName = "Jwt:SigningKey")
    {
        if (environment is null)
        {
            return;
        }

        // Local dev + CI/integration tests legitimately use the dev default.
        if (environment.IsDevelopment()
            || string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw Fail(keyName, environment, "is empty");
        }

        if (Encoding.UTF8.GetBytes(signingKey).Length < 32)
        {
            throw Fail(keyName, environment, "is shorter than the required 32 bytes");
        }

        foreach (var placeholder in KnownPlaceholders)
        {
            if (string.Equals(signingKey, placeholder, StringComparison.Ordinal))
            {
                throw Fail(keyName, environment, "is the committed placeholder/dev key");
            }
        }

        foreach (var marker in PlaceholderMarkers)
        {
            if (signingKey.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                throw Fail(keyName, environment, $"looks like a placeholder (contains '{marker}')");
            }
        }
    }

    private static InvalidOperationException Fail(string keyName, IHostEnvironment environment, string reason)
        => new(
            $"Refusing to start: {keyName} {reason} in the '{environment.EnvironmentName}' environment. "
            + "A real signing key must be injected from configuration/secret (e.g. Jwt__SigningKey) "
            + "before the gateway can run outside Development/Testing. Booting with a known key would "
            + "allow token forgery (SEC-H2).");
}
