using JeebGateway.Availability;
using JeebGateway.FormSubmissions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.FormSubmissions;

public sealed class JeeberOnboardingCoverageResolverTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Unconfigured_behavior_is_explicit(bool failOpen, bool inCoverage, bool checkedCoverage)
    {
        using var services = Provider(new() { FailOpenWhenUnconfigured = failOpen });
        var result = services.GetRequiredService<IJeeberOnboardingCoverageResolver>().Resolve(0, 0);
        Assert.Equal((inCoverage, checkedCoverage, (string?)null), result);
    }

    [Theory]
    [InlineData(33.8, 35.4, "beirut")]
    [InlineData(34, 35.6, "beirut")]
    [InlineData(0, 0, null)]
    public void Configured_bounds_include_edges(double lat, double lng, string? zone)
    {
        using var services = Provider(new() { Boundaries = new() { new ZoneBoundary {
            Key = "beirut", MinLatitude = 33.8, MaxLatitude = 34, MinLongitude = 35.4, MaxLongitude = 35.6 } } });
        Assert.Equal((zone is not null, true, zone), services.GetRequiredService<IJeeberOnboardingCoverageResolver>().Resolve(lat, lng));
    }

    private static ServiceProvider Provider(JeeberOnboardingCoverageOptions options)
    {
        var services = new ServiceCollection();
        services.Configure<JeeberOnboardingCoverageOptions>(o => {
            o.Boundaries = options.Boundaries; o.FailOpenWhenUnconfigured = options.FailOpenWhenUnconfigured; });
        services.AddSingleton<IJeeberOnboardingCoverageResolver, JeeberOnboardingCoverageResolver>();
        return services.BuildServiceProvider();
    }
}
