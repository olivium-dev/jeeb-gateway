namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// T-BE-030 (JEB-66) — gateway-side adapter for the temporary role
/// suspension surface on <c>olivium-dev/user-management</c>:
///   <c>PATCH /api/User/{userId}/role-suspension</c>
///   body { role: "jeeber", reason: "...", duration_hours: 168 }
///
/// Per system-design step 5: "3 strikes / 30 days → temporary suspension
/// of Jeeber role (call user-management <c>available_roles</c> removal of
/// jeeber)". Suspension is time-bounded — the user retains the
/// <c>customer</c> role and can still place client-side deliveries.
///
/// Production wiring swaps the in-memory implementation with an HTTP
/// adapter over the NSwag-generated <c>UserManagementClient</c> (same
/// named HttpClient registered by
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>).
/// </summary>
public interface IJeeberRoleSuspensionClient
{
    /// <summary>
    /// Suspends the <c>jeeber</c> role on <paramref name="userId"/> for
    /// <paramref name="duration"/> starting at <paramref name="at"/>.
    /// Idempotent — a second call with the same userId before the prior
    /// suspension expires overwrites the expiry (extends to a full window
    /// from "now", same semantics as
    /// <see cref="JeebGateway.Requests.Cancellation.IJeeberRestrictionStore"/>).
    /// </summary>
    Task<JeeberRoleSuspensionResult> SuspendAsync(
        string userId,
        DateTimeOffset at,
        TimeSpan duration,
        string reason,
        CancellationToken ct);

    /// <summary>
    /// True when <paramref name="userId"/> currently sits inside an
    /// active jeeber-role suspension window — used by tests and matching
    /// to short-circuit fan-out.
    /// </summary>
    Task<bool> IsSuspendedAsync(string userId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// The wall-clock moment the active suspension expires, or null when
    /// no suspension is in effect.
    /// </summary>
    Task<DateTimeOffset?> GetSuspensionExpiryAsync(
        string userId, DateTimeOffset at, CancellationToken ct);
}

public sealed record JeeberRoleSuspensionResult(
    string UserId,
    DateTimeOffset SuspendedAt,
    DateTimeOffset ExpiresAt,
    string Reason);
