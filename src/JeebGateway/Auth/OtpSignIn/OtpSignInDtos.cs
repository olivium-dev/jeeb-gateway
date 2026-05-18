using System.Text.Json.Serialization;

namespace JeebGateway.Auth.OtpSignIn;

// --- Request DTOs ----------------------------------------------------------

public sealed class OtpRequestDto
{
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
}

public sealed class OtpVerifyDto
{
    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public sealed class OtpRefreshDto
{
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }
}

// --- Response DTOs ---------------------------------------------------------

public sealed class OtpRequestResponse
{
    [JsonPropertyName("ttlSeconds")]
    public int TtlSeconds { get; init; }
}

public sealed class OtpVerifyResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public OtpVerifyUserBlock User { get; init; } = new();
}

public sealed class OtpVerifyUserBlock
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    [JsonPropertyName("activeRole")]
    public string ActiveRole { get; init; } = string.Empty;

    [JsonPropertyName("availableRoles")]
    public string[] AvailableRoles { get; init; } = Array.Empty<string>();
}
