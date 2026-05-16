using JeebGateway.Kyc;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the auth-service Jeeb surface (auth-service/Jeeb/*).
/// Used by the gateway controllers when <c>FeatureFlags:UseUpstream:Auth</c>
/// is set. Methods deserialize directly into the gateway's existing DTOs
/// because the wire shapes line up — see auth-service controllers
/// (JeebKycController, JeebAdminKycController, JeebAuthController).
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx so the
/// controller can decide whether to surface a ProblemDetails or fall back
/// to the in-memory path (it should not — flag=true means upstream owns it).
/// </summary>
public interface IAuthServiceClient
{
    Task<KycSubmissionResponse> SubmitKycAsync(KycSubmitUpstreamRequest request, CancellationToken ct);

    Task<KycStatusResponse> GetKycStatusAsync(string userId, CancellationToken ct);

    Task<KycQueueResponse> AdminKycQueueAsync(int page, int pageSize, CancellationToken ct);

    Task<KycReviewResponse> AdminKycReviewAsync(string submissionId, string actingUserId, KycReviewRequest body, CancellationToken ct);

    Task<TokenRefreshUpstreamResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct);

    Task LogoutAsync(string? refreshToken, CancellationToken ct);
}

/// <summary>
/// Input to <see cref="IAuthServiceClient.SubmitKycAsync"/>. The bytes
/// are buffered in memory because that's what the controller already
/// holds — the upstream auth-service KYC endpoint accepts multipart with
/// the same field names. UserId is forwarded as <c>X-User-Id</c>.
/// </summary>
public sealed class KycSubmitUpstreamRequest
{
    public required string UserId { get; init; }
    public required string VehicleType { get; init; }
    public required string VehicleRegistration { get; init; }
    public required KycFile IdFront { get; init; }
    public required KycFile IdBack { get; init; }
    public required KycFile Selfie { get; init; }
}

public sealed class KycFile
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Bytes { get; init; }
}

public sealed class TokenRefreshUpstreamResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset AccessExpiresAt { get; init; }
    public required DateTimeOffset RefreshExpiresAt { get; init; }
    public required string UserId { get; init; }
    public required string Role { get; init; }
    public bool KycApproved { get; init; }
    public bool PhoneVerified { get; init; }
}
