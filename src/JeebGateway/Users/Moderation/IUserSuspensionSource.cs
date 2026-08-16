namespace JeebGateway.Users.Moderation;

/// <summary>Whether an account is suspended, and the operator-facing reason if it is.</summary>
public readonly record struct UserSuspension(bool IsSuspended, string? Reason)
{
    public static readonly UserSuspension None = new(false, null);
}

/// <summary>
/// The suspension authority for the pre-mint login gate.
///
/// <para><b>Why this is its own interface (D10).</b> <see cref="UserModerationGate"/> used to take
/// an <see cref="IUsersStore"/> and read <c>UserProfile.IsSuspended</c> off it. That is a type
/// which merely <i>happens</i> to carry a suspension field, so the gate compiled and ran against
/// <c>InMemoryUsersStore</c> — a process-local dictionary that no administrator, no CMS and no
/// service can write. Meanwhile the product's real suspend action
/// (<c>PATCH /admin/users/{id}/suspend</c> -> <c>OwnerComposedAdminUsers.SuspendAsync</c> ->
/// <c>IBanServiceClient.ApplyTerminalBanAsync</c>) writes ban-service. Two stores, and the login
/// path read the one nobody writes: Phase V run 2 suspended an account and it logged in anyway.</para>
///
/// <para>A gate that asks for "the users store" can be handed a store with no idea what a
/// suspension is. A gate that asks for a suspension source cannot. Keep it that way: bind this to
/// whatever service OWNS suspension, never to a projection or cache of it.</para>
/// </summary>
public interface IUserSuspensionSource
{
    /// <summary>
    /// Reads the current suspension verdict. A fault MUST propagate — the caller turns it into
    /// <see cref="ModerationVerdict.Unavailable"/> (503), never into a pass. Swallowing here is
    /// the same defect wearing a try/catch.
    /// </summary>
    Task<UserSuspension> ReadAsync(string userId, CancellationToken ct);
}
