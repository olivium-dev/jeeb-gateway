namespace JeebGateway.Tracking;

/// <summary>geolocation-service answered a non-2xx the location seam cannot map onto a value
/// (a 404 on read is still "no fix"). Keeps the generated ApiException — not an
/// HttpRequestException, so it missed every UpstreamExceptionHandler arm — off a courier's
/// phone as an opaque 500.</summary>
public sealed class LocationUpstreamUnavailableException : Exception
{
    public const string ProblemType = "https://jeeb.dev/errors/geolocation-service-unavailable";

    public LocationUpstreamUnavailableException(string member, int statusCode, Exception? inner = null)
        : base($"geolocation-service call '{member}' failed with status {statusCode}.", inner)
    {
        Member = member;
        StatusCode = statusCode;
    }

    public string Member { get; }

    /// <summary>The upstream status. 401/403 mean OUR credential was refused.</summary>
    public int StatusCode { get; }
}
