using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Users;

// F5 avatar contract: ProfilePic stores a bare CDN object ref; every read path projects it here.
// Shape-based: slot ref -> gateway avatar URL, external https -> verbatim, anything else -> null.
public static class AvatarUrlResolver
{
    public const string SlotPrefix = "profile_avatar/";

    private const int VersionTokenLength = 12;
    private const int MinDerivedTokenLength = 8;

    public static string? Absolutize(string? profilePic, string? userId, string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(profilePic))
        {
            return null;
        }

        var trimmed = profilePic.Trim();

        if (trimmed.StartsWith(SlotPrefix, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(publicBaseUrl))
            {
                return null;
            }

            var baseUrl = publicBaseUrl.Trim().TrimEnd('/');
            return $"{baseUrl}/api/users/{Uri.EscapeDataString(userId)}/avatar?v={VersionToken(trimmed)}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || IsAvatarRoutePath(uri.AbsolutePath))
        {
            return null;
        }

        return trimmed;
    }

    // A2: self-referential is detected by PATH SHAPE /api/users/{anyId}/avatar on ANY host.
    public static bool IsSelfReferentialAvatarUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && IsAvatarRoutePath(uri.AbsolutePath);
    }

    private static bool IsAvatarRoutePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4
               && string.Equals(parts[0], "api", StringComparison.OrdinalIgnoreCase)
               && string.Equals(parts[1], "users", StringComparison.OrdinalIgnoreCase)
               && parts[2].Length > 0
               && string.Equals(parts[3], "avatar", StringComparison.OrdinalIgnoreCase);
    }

    // Deterministic cache-busting token: derived from the ref, never from the clock.
    private static string VersionToken(string objectRef)
    {
        var segment = objectRef[(objectRef.LastIndexOf('/') + 1)..];
        var dot = segment.LastIndexOf('.');
        if (dot > 0)
        {
            segment = segment[..dot];
        }

        var alphanumeric = new string(segment.Where(char.IsLetterOrDigit).ToArray());
        if (alphanumeric.Length >= MinDerivedTokenLength)
        {
            return alphanumeric[..Math.Min(VersionTokenLength, alphanumeric.Length)].ToLowerInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(objectRef));
        return Convert.ToHexString(hash)[..VersionTokenLength].ToLowerInvariant();
    }
}
