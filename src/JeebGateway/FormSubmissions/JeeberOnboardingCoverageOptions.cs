using JeebGateway.Availability;
using Microsoft.Extensions.Options;

namespace JeebGateway.FormSubmissions;

public sealed class JeeberOnboardingCoverageOptions
{
    public const string SectionName = "JeeberOnboarding:Coverage";
    public List<ZoneBoundary> Boundaries { get; set; } = new();
    public bool FailOpenWhenUnconfigured { get; set; } = true;
}
public interface IJeeberOnboardingCoverageResolver
{
    (bool InCoverage, bool Checked, string? ZoneKey) Resolve(double latitude, double longitude);
}

public sealed class JeeberOnboardingCoverageResolver(
    IOptionsMonitor<JeeberOnboardingCoverageOptions> options) : IJeeberOnboardingCoverageResolver
{
    public (bool InCoverage, bool Checked, string? ZoneKey) Resolve(double latitude, double longitude)
    {
        var current = options.CurrentValue;
        if (current.Boundaries.Count == 0)
            return (current.FailOpenWhenUnconfigured, !current.FailOpenWhenUnconfigured, null);
        var zone = current.Boundaries.FirstOrDefault(z => z.Contains(latitude, longitude));
        return (zone is not null, true, zone?.Key);
    }
}
