using JeebGateway.Users;

namespace JeebGateway.Push;

/// <summary>
/// JEB-1486 boundary remediation — the Jeeb <c>active_role -&gt; FCM topic</c>
/// routing lives HERE, in the gateway, NOT in the shared push-notification
/// service (GR2). push-notification is a generic FCM relay that subscribes
/// devices to OPAQUE topic strings; the gateway decides which topic a given
/// Jeeb role maps to and passes it explicitly on device registration.
///
/// The two Jeeb audience topics (<c>jeeb_clients</c> / <c>jeeb_jeebers</c>) are
/// the product topic-group names the push relay hosts; they are opaque to the
/// relay and meaningful only here.
/// </summary>
public static class JeebPushTopicMap
{
    /// <summary>FCM topic for Jeeb clients (the role that places requests).</summary>
    public const string ClientsTopic = "jeeb_clients";

    /// <summary>FCM topic for Jeebers (the role that fulfils deliveries).</summary>
    public const string JeebersTopic = "jeeb_jeebers";

    /// <summary>
    /// Map a user's active role to its FCM topic, or <c>null</c> when the role
    /// has no audience topic. Accepts the opaque role persisted by
    /// user-management (<see cref="Roles.Client"/>/<see cref="Roles.Jeeber"/> =
    /// customer/driver), the frozen Jeeb contract role (client/jeeber), and the
    /// legacy push role strings (jeeb_client/jeeb_jeeber) — all case-insensitive.
    /// </summary>
    public static string? TopicForRole(string? activeRole)
    {
        if (string.IsNullOrWhiteSpace(activeRole)) return null;
        var r = activeRole.Trim();

        if (Matches(r, Roles.Client, JeebRoleTranslator.ContractClient, "jeeb_client"))
            return ClientsTopic;
        if (Matches(r, Roles.Jeeber, JeebRoleTranslator.ContractJeeber, "jeeb_jeeber"))
            return JeebersTopic;

        return null;
    }

    private static bool Matches(string role, params string[] accepted)
    {
        foreach (var a in accepted)
        {
            if (string.Equals(role, a, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
