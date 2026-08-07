namespace JeebGateway.Notifications;

/// <summary>Hardened user-id equality: Guid equality when both sides parse (case/format
/// insensitive), else trimmed OrdinalIgnoreCase. The single comparator for actor exclusion.</summary>
public static class UserIdComparison
{
    public static bool SameUser(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var left = a.Trim();
        var right = b.Trim();

        if (Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid))
        {
            return leftGuid == rightGuid;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
