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
}
