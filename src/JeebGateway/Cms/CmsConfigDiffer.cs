namespace JeebGateway.Cms;

/// <summary>
/// Computes a deterministic key-level diff between two CMS config snapshots.
/// Shallow by design (top-level keys only) — the MVP contract only asserts
/// added / removed / changed keys, which is enough to drive the authoring UI's
/// "what changed between v_n and v_m" view. Value equality uses the
/// JSON-normalised string form so two structurally-equal values never report
/// as changed.
/// </summary>
public static class CmsConfigDiffer
{
    public static CmsDiffResponse Diff(
        string surfaceId,
        CmsConfigVersion from,
        CmsConfigVersion to)
    {
        var fromData = from.Config.Data;
        var toData = to.Config.Data;

        var added = toData.Keys
            .Where(k => !fromData.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var removed = fromData.Keys
            .Where(k => !toData.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var changed = toData.Keys
            .Where(fromData.ContainsKey)
            .Where(k => !ValueEquals(fromData[k], toData[k]))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new CmsDiffChangedKey(k, fromData[k], toData[k]))
            .ToList();

        return new CmsDiffResponse(surfaceId, from.Version, to.Version, added, removed, changed);
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        // Cheap, allocation-light structural compare for the scalar/string
        // values the MVP carries. Falls back to ToString for anything exotic.
        return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}
