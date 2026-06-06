namespace JeebGateway.Users;

/// <summary>
/// S02 Wave-1 (ADR-003) translation seam — the ONLY place in the fleet where the
/// frozen Jeeb client contract vocabulary <c>{client, jeeber}</c> is bound to the
/// product-agnostic OPAQUE role strings persisted by the shared
/// <c>user-management</c> service (<c>{customer, driver}</c>, see <see cref="Roles"/>).
///
/// <para><b>N14 invariant.</b> user-management never names <c>client</c>/<c>jeeber</c>.
/// The gateway is a thin BFF that TRANSLATES: opaque -&gt; snake_case on the way out
/// (response bodies the Jeeb mobile client / :3040 console consume), and snake_case
/// -&gt; opaque on the way in (the role string forwarded to UM on a role switch).
/// This whitelist therefore lives ONLY in the gateway diff; grepping UM for
/// <c>client</c>/<c>jeeber</c> must return nothing.</para>
///
/// <para><b>Mapping.</b>
/// <list type="bullet">
///   <item><description>opaque <c>customer</c> (<see cref="Roles.Client"/>) &lt;-&gt; contract <c>client</c></description></item>
///   <item><description>opaque <c>driver</c> (<see cref="Roles.Jeeber"/>) &lt;-&gt; contract <c>jeeber</c></description></item>
/// </list>
/// Any other opaque role (e.g. <c>admin</c>) passes through unchanged on the way
/// out; on the way in, only the two Jeeb contract roles are accepted (the gateway
/// rejects everything else as <c>invalid_role</c> 400 BEFORE any UM call — N6).</para>
/// </summary>
public static class JeebRoleTranslator
{
    /// <summary>Frozen Jeeb client-contract role: a customer placing delivery requests.</summary>
    public const string ContractClient = "client";

    /// <summary>Frozen Jeeb client-contract role: a Jeeber fulfilling deliveries.</summary>
    public const string ContractJeeber = "jeeber";

    /// <summary>
    /// The closed set of Jeeb contract roles the gateway accepts on an INBOUND
    /// role switch. Used by F-A to fail <c>invalid_role</c> (400, no UM call)
    /// before translating. Case-insensitive lookups via <see cref="IsContractRole"/>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ContractRoles =
        new[] { ContractClient, ContractJeeber };

    /// <summary>
    /// Translate one OPAQUE role (as persisted by user-management) to the Jeeb
    /// snake_case contract role. Unknown roles (e.g. <c>admin</c>) pass through
    /// verbatim so non-Jeeb operator roles are not silently dropped from a body.
    /// </summary>
    public static string ToContract(string? opaqueRole)
    {
        if (string.IsNullOrWhiteSpace(opaqueRole)) return string.Empty;
        var r = opaqueRole.Trim();
        if (string.Equals(r, Roles.Client, StringComparison.OrdinalIgnoreCase)) return ContractClient;
        if (string.Equals(r, Roles.Jeeber, StringComparison.OrdinalIgnoreCase)) return ContractJeeber;
        return r;
    }

    /// <summary>Translate a set of opaque roles to the snake_case contract, order-preserving and de-duplicated.</summary>
    public static string[] ToContract(IEnumerable<string>? opaqueRoles)
    {
        if (opaqueRoles is null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var role in opaqueRoles)
        {
            var contract = ToContract(role);
            if (contract.Length == 0) continue;
            if (seen.Add(contract)) result.Add(contract);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Translate one INBOUND Jeeb snake_case contract role to the OPAQUE role
    /// user-management understands. Returns <c>null</c> when the input is not a
    /// recognised Jeeb contract role — the caller (F-A) maps that to
    /// <c>invalid_role</c> 400 WITHOUT calling UM (N6).
    /// </summary>
    public static string? ToOpaque(string? contractRole)
    {
        if (string.IsNullOrWhiteSpace(contractRole)) return null;
        var r = contractRole.Trim();
        if (string.Equals(r, ContractClient, StringComparison.OrdinalIgnoreCase)) return Roles.Client;
        if (string.Equals(r, ContractJeeber, StringComparison.OrdinalIgnoreCase)) return Roles.Jeeber;
        return null;
    }

    /// <summary>True when <paramref name="contractRole"/> is one of the two frozen Jeeb contract roles.</summary>
    public static bool IsContractRole(string? contractRole) => ToOpaque(contractRole) is not null;
}
