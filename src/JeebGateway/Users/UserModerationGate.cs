namespace JeebGateway.Users;

/// <summary>Outcome of the pre-mint moderation check; only Proceed may issue a session.</summary>
public enum ModerationVerdict
{
    Proceed,
    Suspended,

    /// <summary>Account status could not be established — callers must fail CLOSED.</summary>
    Unavailable,
}

/// <summary>
/// The ONE pre-mint suspension check shared by every path that issues a gateway session.
/// Admin suspend revokes only REFRESH tokens, so any ungated mint re-opens the account.
/// </summary>
public static class UserModerationGate
{
    /// <summary>Reason surfaced when an operator recorded none.</summary>
    public const string DefaultReason = "Contact support.";

    /// <summary>
    /// A lookup fault is Unavailable, NOT a silent pass. A genuinely absent user still
    /// proceeds, so a first-time login is unaffected.
    /// </summary>
    public static async Task<(ModerationVerdict Verdict, string Reason)> EvaluateAsync(
        IUsersStore users, string? userId, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return (ModerationVerdict.Proceed, string.Empty);

        UserProfile? profile;
        try
        {
            profile = await users.GetForModerationAsync(userId!, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "moderation lookup failed userId={UserId}; refusing to mint a session", userId);
            return (ModerationVerdict.Unavailable, string.Empty);
        }

        if (profile is not { IsSuspended: true }) return (ModerationVerdict.Proceed, string.Empty);

        // SetSuspensionAsync persists reason ?? "" to satisfy the 0012 CHECK, so a blank
        // is reachable and must not reach the client as an empty detail.
        var reason = string.IsNullOrWhiteSpace(profile.SuspensionReason)
            ? DefaultReason
            : profile.SuspensionReason!;
        return (ModerationVerdict.Suspended, reason);
    }
}
