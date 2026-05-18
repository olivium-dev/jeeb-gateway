namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Domain contract for the sibling story <c>T-BE-001a</c> endpoint on
/// <c>olivium-dev/user-management</c>:
///   <c>POST /api/users/phone-identity/find-or-create</c>
///   body  { phone: "+961xxxxxxxx" }
///   200   { userId, isNew, phone, available_roles, active_role }
///   400   Problem(type=invalid_phone)
///
/// At the time JEB-471 ships, <c>T-BE-001a</c> is in flight on
/// <c>feature/jeeb-v3-dual-role-identity</c> in <c>olivium-dev/user-management</c>.
/// The gateway DI registration falls back to <see cref="NotConfiguredUserManagementPhoneIdentityClient"/>
/// when <c>UserManagementApi:BaseUrl</c> is unset so local dev / integration
/// tests can stub the client; production wiring swaps in the NSwag-generated
/// <c>UserManagementClient.PhoneIdentityFindOrCreateAsync</c> behind a thin
/// adapter implementing this interface once the sibling PR lands.
/// </summary>
public interface IUserManagementPhoneIdentityClient
{
    Task<PhoneIdentityResponse> PhoneIdentityFindOrCreateAsync(
        string normalizedE164Phone,
        CancellationToken ct = default);
}

public sealed record PhoneIdentityResponse(
    Guid UserId,
    bool IsNew,
    string Phone,
    string[] AvailableRoles,
    string ActiveRole);

/// <summary>
/// Default registration used until the sibling story <c>T-BE-001a</c> lands
/// the real NSwag-generated client. Returns a deterministic 500 so the
/// gateway fails closed rather than silently issuing tokens against an
/// unverified identity store.
/// </summary>
public sealed class NotConfiguredUserManagementPhoneIdentityClient
    : IUserManagementPhoneIdentityClient
{
    public Task<PhoneIdentityResponse> PhoneIdentityFindOrCreateAsync(
        string normalizedE164Phone, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "UserManagementApi:BaseUrl is not configured. The OTP verify path " +
            "requires the sibling olivium-dev/user-management story T-BE-001a " +
            "(POST /api/users/phone-identity/find-or-create). Register an " +
            "IUserManagementPhoneIdentityClient implementation (typically the " +
            "NSwag-generated UserManagementClient wrapped in an adapter) before " +
            "this endpoint is reachable in production.");
}
