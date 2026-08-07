using System.Security.Cryptography;

namespace JeebGateway.Security;

internal static class AdminSessionCookies
{
    internal const string RefreshCookie = "__Host-jeeb_admin_rt";
    internal const string CsrfCookie = "__Host-jeeb_admin_csrf";
    internal const string CsrfHeader = "X-Jeeb-CSRF";
    internal const string OidcCorrelationCookie = "__Host-jeeb_admin_oidc";

    internal static void Set(HttpRequest request, HttpResponse response, string refreshToken, bool rotateCsrf = true)
    {
        response.Cookies.Append(RefreshCookie, refreshToken, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
        if (rotateCsrf || !request.Cookies.ContainsKey(CsrfCookie))
        {
            response.Cookies.Append(CsrfCookie, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), new CookieOptions
            {
                Secure = true,
                HttpOnly = false,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });
        }
    }

    internal static void Delete(HttpResponse response)
    {
        var options = new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" };
        response.Cookies.Delete(RefreshCookie, options);
        response.Cookies.Delete(CsrfCookie, options);
    }

    internal static void SetOidcCorrelation(HttpResponse response, string protectedValue, TimeSpan lifetime)
    {
        response.Cookies.Append(OidcCorrelationCookie, protectedValue, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = lifetime,
        });
    }

    internal static void DeleteOidcCorrelation(HttpResponse response) =>
        response.Cookies.Delete(OidcCorrelationCookie, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
}
