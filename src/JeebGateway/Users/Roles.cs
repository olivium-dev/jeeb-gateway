namespace JeebGateway.Users;

/// <summary>
/// Canonical role names used across the gateway. Matches the values
/// persisted in <c>users.roles</c> (db/migrations/0001) and emitted by
/// auth-service into the JWT "roles" claim.
///
/// A single account may carry any combination of these — a user can be
/// both a Client and a Jeeber on the same id (T-backend-041).
/// </summary>
public static class Roles
{
    /// <summary>Acts as a Client placing delivery requests (BR-1, BR-9).</summary>
    public const string Client = "customer";

    /// <summary>Acts as a Jeeber fulfilling deliveries (availability toggle, offer-service).</summary>
    public const string Jeeber = "driver";

    /// <summary>Internal operator with access to /admin/** endpoints.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Jeeb Partner (cash shop / agent) who accepts cash offline and tops up jeeber
    /// wallets digitally through the Partner Portal (JEBV4 partner-wallet-bff).
    ///
    /// <para>Like <see cref="Client"/>/<see cref="Jeeber"/> this is the OPAQUE role string
    /// as persisted by the shared user-management service; unlike them it needs no
    /// snake_case contract translation (a partner never places delivery requests), so
    /// <see cref="JeebRoleTranslator.ToContract"/> passes it through verbatim — exactly the
    /// documented pass-through for any non-<c>{customer,driver}</c> role (e.g. <c>admin</c>).
    /// Referenced only by the ADR-005 capability map for the <c>partner.*</c> capabilities;
    /// additive, changes no existing role handling.</para>
    /// </summary>
    public const string Partner = "partner";
}
