using FluentAssertions;
using JeebGateway.Controllers.V1;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>Unit coverage for the orders-list CreatedAt range predicate.</summary>
public sealed class OrderDateRangeFilterTests
{
    [Fact]
    public void Includes_Row_At_Inclusive_Lower_Edge()
    {
        var fromDate = DateTimeOffset.Parse("2026-07-21T00:00:00Z");

        OrderDateRangeFilter.Includes(fromDate, fromDate, fromDate.AddDays(1))
            .Should().BeTrue();
    }

    [Fact]
    public void Same_Day_Range_Includes_Whole_Day_But_Excludes_Next_Midnight()
    {
        var fromDate = DateTimeOffset.Parse("2026-07-21T00:00:00Z");
        var toDate = fromDate.AddDays(1);

        OrderDateRangeFilter.Includes(toDate.AddTicks(-1), fromDate, toDate)
            .Should().BeTrue();
        OrderDateRangeFilter.Includes(toDate, fromDate, toDate)
            .Should().BeFalse();
    }

    [Fact]
    public void Null_Bounds_Do_Not_Constrain_That_Side()
    {
        var row = DateTimeOffset.Parse("2026-07-21T12:00:00Z");

        OrderDateRangeFilter.Includes(row, null, null).Should().BeTrue();
        OrderDateRangeFilter.Includes(row, null, row.AddHours(1)).Should().BeTrue();
        OrderDateRangeFilter.Includes(row, row.AddHours(-1), null).Should().BeTrue();
    }

    [Fact]
    public void Includes_Mixed_Offsets_By_Comparing_Utc_Instants()
    {
        var fromDate = DateTimeOffset.Parse("2026-07-21T00:00:00+02:00");
        var toDate = DateTimeOffset.Parse("2026-07-22T00:00:00+02:00");
        var utcRow = DateTimeOffset.Parse("2026-07-21T21:59:59Z");

        OrderDateRangeFilter.Includes(utcRow, fromDate, toDate).Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-07-21T00:00:00Z", "2026-07-21T00:00:00Z")]
    [InlineData("2026-07-21T00:00:00Z", "2026-07-20T23:59:59Z")]
    public void Rejects_Empty_Or_Inverted_Range(string from, string to)
    {
        OrderDateRangeFilter.IsValid(DateTimeOffset.Parse(from), DateTimeOffset.Parse(to))
            .Should().BeFalse();
    }
}
