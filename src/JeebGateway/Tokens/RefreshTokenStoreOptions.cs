using Microsoft.Extensions.Configuration;

namespace JeebGateway.Tokens;

/// <summary>A10 ordered ladder for the refresh-token domain, low to high. Pinned at
/// <see cref="Local"/> — today's wiring — until the owner flips it wave by wave.</summary>
public enum RefreshTokenStoreMode
{
    // dual-write-local-read shares Local's wiring: the local store is process memory, not a
    // table, so there is nothing to dual-write — state-service owns the write once wired.
    Local = 0,
    DualWriteLocalRead = 1,
    DualWriteUpstreamRead = 2,
    UpstreamAuthority = 3,
    Retired = 4,
}

/// <summary>The ONE A10 control for the refresh-token store (no UseUpstream pair):
/// <c>FeatureFlags:RefreshTokenStoreMode</c>, validated at startup.</summary>
public sealed class RefreshTokenStoreOptions
{
    public const string SectionName = "FeatureFlags";
    public const string ModeKey = "FeatureFlags:RefreshTokenStoreMode";
    public const string ModeNames =
        "local, dual-write-local-read, dual-write-upstream-read, upstream-authority, retired";

    /// <summary>Wire spelling of the ladder rung; unset means <c>local</c>.</summary>
    public string? RefreshTokenStoreMode { get; set; }
}

/// <summary>Wire spelling to ladder rung. These five values are the only accepted ones —
/// anything else fails <c>ValidateOnStart</c> rather than picking a store.</summary>
public static class RefreshTokenStoreModes
{
    private static readonly IReadOnlyDictionary<string, RefreshTokenStoreMode> ByWireName =
        new Dictionary<string, RefreshTokenStoreMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = RefreshTokenStoreMode.Local,
            ["dual-write-local-read"] = RefreshTokenStoreMode.DualWriteLocalRead,
            ["dual-write-upstream-read"] = RefreshTokenStoreMode.DualWriteUpstreamRead,
            ["upstream-authority"] = RefreshTokenStoreMode.UpstreamAuthority,
            ["retired"] = RefreshTokenStoreMode.Retired,
        };

    public static bool TryParse(string? wireValue, out RefreshTokenStoreMode mode)
    {
        if (string.IsNullOrWhiteSpace(wireValue))
        {
            mode = RefreshTokenStoreMode.Local;
            return true;
        }

        return ByWireName.TryGetValue(wireValue.Trim(), out mode);
    }

    /// <summary>Registration-time read. A typo resolves to the pinned default and
    /// <c>ValidateOnStart</c> then refuses the boot, so no store is picked off it.</summary>
    public static RefreshTokenStoreMode Resolve(IConfiguration configuration)
        => TryParse(configuration[RefreshTokenStoreOptions.ModeKey], out var mode)
            ? mode
            : RefreshTokenStoreMode.Local;

    /// <summary>From the READ FLIP up, upstream serves reads: no in-memory fallback is
    /// registered, so the dependency must be wired or the boot fails.</summary>
    public static bool RequiresStateService(RefreshTokenStoreMode mode)
        => mode >= RefreshTokenStoreMode.DualWriteUpstreamRead;
}
