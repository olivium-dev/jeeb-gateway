using JeebGateway.Requests;

namespace JeebGateway.Users;

/// <summary>
/// BR-1 enforcement (T-backend-041): a user cannot act as both Client and
/// Jeeber simultaneously in the same delivery. This service validates that
/// a role switch is safe (no active deliveries under the current role) and
/// that a user cannot participate on both sides of a single delivery.
/// </summary>
public interface IDualRoleService
{
    /// <summary>
    /// Validates whether <paramref name="userId"/> can switch to
    /// <paramref name="targetRole"/>. Returns a <see cref="RoleSwitchResult"/>
    /// with the outcome — callers inspect <see cref="RoleSwitchResult.IsAllowed"/>
    /// and surface the denial reason as ProblemDetails when false.
    /// </summary>
    Task<RoleSwitchResult> ValidateRoleSwitchAsync(string userId, string targetRole, CancellationToken ct);

    /// <summary>
    /// BR-1 delivery-level check: ensures <paramref name="userId"/> is not
    /// the Client of a delivery they are about to accept as Jeeber (and
    /// vice versa). Returns true when the participation would violate BR-1.
    /// </summary>
    Task<bool> WouldViolateSameDeliveryRuleAsync(string userId, string requestId, CancellationToken ct);
}

public class DualRoleService : IDualRoleService
{
    private readonly IUsersStore _users;
    private readonly IRequestsStore _requests;

    public DualRoleService(IUsersStore users, IRequestsStore requests)
    {
        _users = users;
        _requests = requests;
    }

    public async Task<RoleSwitchResult> ValidateRoleSwitchAsync(
        string userId, string targetRole, CancellationToken ct)
    {
        var profile = await _users.GetByIdAsync(userId, ct);
        if (profile is null)
        {
            return RoleSwitchResult.Denied("User not found.");
        }

        if (!profile.Roles.Contains(targetRole, StringComparer.OrdinalIgnoreCase))
        {
            return RoleSwitchResult.Denied(
                $"User does not hold the '{targetRole}' role. " +
                $"Current roles: {string.Join(", ", profile.Roles)}.");
        }

        if (string.Equals(profile.ActiveRole, targetRole, StringComparison.OrdinalIgnoreCase))
        {
            return RoleSwitchResult.Denied($"Already operating as '{targetRole}'.");
        }

        if (string.Equals(profile.ActiveRole, Roles.Client, StringComparison.OrdinalIgnoreCase))
        {
            var activeClientRequests = await _requests.CountActiveForClientAsync(userId, ct);
            if (activeClientRequests > 0)
            {
                return RoleSwitchResult.Denied(
                    $"Cannot switch to {targetRole}: {activeClientRequests} active delivery " +
                    $"request(s) as Client. Complete or cancel them first.");
            }
        }

        if (string.Equals(profile.ActiveRole, Roles.Jeeber, StringComparison.OrdinalIgnoreCase))
        {
            var activeJeeberDeliveries = await _requests.CountActiveForJeeberAsync(userId, ct);
            if (activeJeeberDeliveries > 0)
            {
                return RoleSwitchResult.Denied(
                    $"Cannot switch to {targetRole}: {activeJeeberDeliveries} active delivery(ies) " +
                    $"as Jeeber. Complete them first.");
            }
        }

        return RoleSwitchResult.Allowed(profile.ActiveRole, targetRole);
    }

    public async Task<bool> WouldViolateSameDeliveryRuleAsync(
        string userId, string requestId, CancellationToken ct)
    {
        var request = await _requests.GetAsync(requestId, ct);
        if (request is null) return false;

        // User is the Client of this delivery → cannot also be its Jeeber.
        if (string.Equals(request.ClientId, userId, StringComparison.Ordinal))
            return true;

        // User is already the Jeeber of this delivery → cannot also be its Client.
        if (string.Equals(request.JeeberId, userId, StringComparison.Ordinal))
            return true;

        return false;
    }
}

public class RoleSwitchResult
{
    public bool IsAllowed { get; init; }
    public string? DenialReason { get; init; }
    public string? PreviousRole { get; init; }
    public string? NewRole { get; init; }

    public static RoleSwitchResult Allowed(string previousRole, string newRole) => new()
    {
        IsAllowed = true,
        PreviousRole = previousRole,
        NewRole = newRole
    };

    public static RoleSwitchResult Denied(string reason) => new()
    {
        IsAllowed = false,
        DenialReason = reason
    };
}

/// <summary>
/// Thrown by the offer-accept path when BR-1 is violated: a user cannot be
/// both Client and Jeeber of the same delivery.
/// </summary>
public class SameDeliveryRoleViolationException : Exception
{
    public string UserId { get; }
    public string RequestId { get; }

    public SameDeliveryRoleViolationException(string userId, string requestId)
        : base($"BR-1 violation: user '{userId}' cannot act as both Client and Jeeber on delivery '{requestId}'.")
    {
        UserId = userId;
        RequestId = requestId;
    }
}
