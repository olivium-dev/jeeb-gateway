using JeebGateway.Users.Moderation;

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
///
/// <para><b>D10.</b> This gate used to take an <see cref="IUsersStore"/> and read
/// <c>UserProfile.IsSuspended</c>. In production that store is <c>InMemoryUsersStore</c> — a
/// process-local dictionary nothing outside the gateway can write — while the product's suspend
/// action writes ban-service. The gate ran, found nothing, and admitted suspended accounts. It now
/// takes an <see cref="IUserSuspensionSource"/>, a type that cannot be satisfied by something with
/// no notion of suspension.</para>
/// </summary>
public static class UserModerationGate
{
    /// <summary>Reason surfaced when an operator recorded none.</summary>
    public const string DefaultReason = "Contact support.";

    /// <summary>
    /// A lookup fault is Unavailable, NOT a silent pass. A user with no suspension on record
    /// still proceeds, so a first-time login is unaffected.
    /// </summary>
    public static async Task<(ModerationVerdict Verdict, string Reason)> EvaluateAsync(
        IUserSuspensionSource suspensions, string? userId, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return (ModerationVerdict.Proceed, string.Empty);

        UserSuspension suspension;
        try
        {
            suspension = await suspensions.ReadAsync(userId!, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "moderation lookup failed userId={UserId}; refusing to mint a session", userId);
            return (ModerationVerdict.Unavailable, string.Empty);
        }

        if (!suspension.IsSuspended) return (ModerationVerdict.Proceed, string.Empty);

        // ban-service supplies the configured policy message, which can be blank; a blank must
        // not reach the client as an empty detail.
        var reason = string.IsNullOrWhiteSpace(suspension.Reason)
            ? DefaultReason
            : suspension.Reason!;
        return (ModerationVerdict.Suspended, reason);
    }
}
