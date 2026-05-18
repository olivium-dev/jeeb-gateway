// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — Ported from updated-requirements/qa-scaffolding/JEB-467/
//   auth-service/AuthService.Tests/Otp/Fixtures/FakeUserManagementClient.cs
//
// Port adjustments:
//   - Namespace: AuthService.Tests.Otp.Fixtures → JeebGateway.IntegrationTests.OtpSignIn.Fixtures
//   - Implements production IUserManagementPhoneIdentityClient (not the
//     scaffolding's IFakeUserManagementClient marker interface).
//   - Reuses production PhoneIdentityResponse record.
//
// Mirrors the sibling story JEB-1422 (T-BE-001a-user-mgmt-phone-identity)
// contract pinned in audit #14764:
//   POST /api/users/phone-identity/find-or-create
//   body { "phone": "+961xxxxxxxx" }
//   200  { userId, isNew, phone, available_roles, active_role }

using System.Collections.Concurrent;
using JeebGateway.Auth.OtpSignIn;

namespace JeebGateway.IntegrationTests.OtpSignIn.Fixtures;

public sealed class FakeUserManagementClient : IUserManagementPhoneIdentityClient
{
    private readonly ConcurrentDictionary<string, PhoneIdentity> _store = new();

    public Task<PhoneIdentityResponse> PhoneIdentityFindOrCreateAsync(
        string normalizedE164Phone,
        CancellationToken ct = default)
    {
        var wasNew  = !_store.ContainsKey(normalizedE164Phone);
        var identity = _store.GetOrAdd(normalizedE164Phone, p => new PhoneIdentity(
            UserId:         Guid.NewGuid(),
            Phone:          p,
            CreatedAt:      DateTimeOffset.UtcNow,
            AvailableRoles: new[] { "customer", "rider" },
            ActiveRole:     "customer"));

        FindOrCreateCalls.Add((normalizedE164Phone, identity.UserId, wasNew));

        return Task.FromResult(new PhoneIdentityResponse(
            UserId:         identity.UserId,
            IsNew:          wasNew,
            Phone:          identity.Phone,
            AvailableRoles: identity.AvailableRoles,
            ActiveRole:     identity.ActiveRole));
    }

    public List<(string Phone, Guid UserId, bool IsNew)> FindOrCreateCalls { get; } = new();

    public void Reset()
    {
        FindOrCreateCalls.Clear();
        _store.Clear();
    }

    private sealed record PhoneIdentity(
        Guid UserId,
        string Phone,
        DateTimeOffset CreatedAt,
        string[] AvailableRoles,
        string ActiveRole);
}
