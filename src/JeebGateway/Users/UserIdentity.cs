using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Users;

/// <summary>
/// Shared identity resolution used by every authenticated endpoint on
/// the gateway. Mirrors the pattern in NotificationPreferencesController:
///   1. trust the JWT subject claim when present
///   2. fall back to the X-User-Id header injected by the edge (MVP)
///   3. otherwise return 401
///
/// Roles come from the "roles" claim (multi-valued) when available, and
/// from the X-User-Roles header (comma-separated) for the MVP fallback.
/// </summary>
internal static class UserIdentity
{
    public static bool TryGetUserId(HttpContext httpContext, out string userId, out IActionResult problem)
    {
        var fromClaim = httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? httpContext.User?.FindFirstValue("sub");

        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            userId = fromClaim;
            problem = null!;
            return true;
        }

        if (httpContext.Request.Headers.TryGetValue("X-User-Id", out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            userId = header.ToString();
            problem = null!;
            return true;
        }

        userId = string.Empty;
        problem = new UnauthorizedResult();
        return false;
    }

    public static IReadOnlyList<string> GetRoles(HttpContext httpContext)
    {
        var claimed = httpContext.User?
            .FindAll(ClaimTypes.Role)
            .Concat(httpContext.User.FindAll("roles"))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList() ?? new List<string>();

        if (claimed.Count > 0) return claimed;

        if (httpContext.Request.Headers.TryGetValue("X-User-Roles", out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return Array.Empty<string>();
    }

    public static bool HasRole(HttpContext httpContext, string role)
    {
        var roles = GetRoles(httpContext);
        return roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasAnyRole(HttpContext httpContext, params string[] anyOf)
    {
        if (anyOf.Length == 0) return true;
        var roles = GetRoles(httpContext);
        return roles.Any(r => anyOf.Any(want => string.Equals(r, want, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsAdmin(HttpContext httpContext) => HasRole(httpContext, Roles.Admin);
}
