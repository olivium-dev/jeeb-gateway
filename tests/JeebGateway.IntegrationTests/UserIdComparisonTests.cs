using FluentAssertions;
using JeebGateway.Notifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// The one hardened user-id comparator every push actor-exclusion now rides
/// (<see cref="UserIdComparison.SameUser"/>). Format/case skew between id sources
/// (JWT sub vs store rows vs upstream party ids) must never reopen a self-notify path.
/// </summary>
public class UserIdComparisonTests
{
    private const string GuidLower = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    [Theory]
    [InlineData(GuidLower, "7C9E6679-7425-40DE-944B-E07FC1F90AE7")] // case skew
    [InlineData(GuidLower, "7c9e6679742540de944be07fc1f90ae7")]     // N-format skew
    [InlineData(GuidLower, "{7c9e6679-7425-40de-944b-e07fc1f90ae7}")] // B-format skew
    [InlineData(GuidLower, "  7c9e6679-7425-40de-944b-e07fc1f90ae7  ")] // whitespace
    [InlineData("user-abc", "USER-ABC")]
    [InlineData("user-abc", " user-abc ")]
    public void SameUser_True_Across_Format_And_Case_Skew(string a, string b)
    {
        UserIdComparison.SameUser(a, b).Should().BeTrue();
        UserIdComparison.SameUser(b, a).Should().BeTrue();
    }

    [Theory]
    [InlineData(GuidLower, "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("user-abc", "user-abd")]
    [InlineData(GuidLower, "user-abc")]
    public void SameUser_False_For_Different_Users(string a, string b)
    {
        UserIdComparison.SameUser(a, b).Should().BeFalse();
        UserIdComparison.SameUser(b, a).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, GuidLower)]
    [InlineData("", GuidLower)]
    [InlineData("   ", GuidLower)]
    [InlineData(null, null)]
    public void SameUser_False_On_Blank_Sides_Never_Throws(string? a, string? b)
    {
        UserIdComparison.SameUser(a, b).Should().BeFalse();
        UserIdComparison.SameUser(b, a).Should().BeFalse();
    }
}
