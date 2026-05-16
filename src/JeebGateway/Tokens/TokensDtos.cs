namespace JeebGateway.Tokens;

public class IssueTokensRequest
{
    public string? UserId { get; set; }
    public IReadOnlyList<string>? Roles { get; set; }
}

public class RefreshTokenRequest
{
    public string? RefreshToken { get; set; }
}

public class RevokeTokenRequest
{
    public string? RefreshToken { get; set; }
}

public class TokenPairResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required string TokenType { get; init; } = "Bearer";
    public required int AccessTokenExpiresInSeconds { get; init; }
    public required DateTimeOffset AccessTokenExpiresAt { get; init; }
    public required DateTimeOffset RefreshTokenExpiresAt { get; init; }
}

public class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

public class ChangePhoneRequest
{
    public string? NewPhone { get; set; }
    public string? OtpCode { get; set; }
}
