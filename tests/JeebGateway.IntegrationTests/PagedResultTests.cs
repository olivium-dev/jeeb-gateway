using FluentAssertions;
using JeebGateway.Infrastructure.Pagination;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-08 / SYS-04 — the canonical list envelope (<see cref="PagedResult{T}"/>) and
/// its clamped <see cref="PageRequest"/>. These guard the two invariants the
/// client's infinite-scroll loader relies on: (1) a uniform shape across every
/// feed, and (2) a <c>HasMore</c> that terminates the scroll correctly on a
/// partially-filled final page. The clamp tests prove a hostile <c>?pageSize</c>
/// can never become an unbounded window.
/// </summary>
public sealed class PagedResultTests
{
    [Theory]
    [InlineData(1, 20, 100, true)]   // first of five full pages
    [InlineData(5, 20, 100, false)]  // exact last page — no more
    [InlineData(5, 20, 95, false)]   // short final page (95 items) — still terminates
    [InlineData(4, 20, 95, true)]    // page 4 of a 95-item set — one short page left
    [InlineData(1, 20, 0, false)]    // empty result set — never "load more"
    public void HasMore_Is_Derived_From_The_Window_Not_The_Item_Count(
        int page, int pageSize, int total, bool expectedHasMore)
    {
        var request = PageRequest.Of(page, pageSize);
        var result = PagedResult<int>.Create(System.Array.Empty<int>(), request, total);

        result.HasMore.Should().Be(expectedHasMore);
        result.Page.Should().Be(page);
        result.PageSize.Should().Be(pageSize);
        result.Total.Should().Be(total);
    }

    [Fact]
    public void From_Windows_An_InMemory_Sequence_Onto_The_Requested_Page()
    {
        var source = Enumerable.Range(1, 55).ToList(); // 1..55

        var page3 = PagedResult<int>.From(source, PageRequest.Of(3, 20));

        page3.Items.Should().Equal(41, 42, 43, 44, 45, 46, 47, 48, 49, 50,
                                   51, 52, 53, 54, 55); // last 15 items
        page3.Total.Should().Be(55);
        page3.HasMore.Should().BeFalse("page 3 of a 55-item, size-20 set is the final page");
    }

    [Fact]
    public void Empty_Source_Yields_An_Empty_First_Page_Not_Null()
    {
        var page = PagedResult<string>.From(System.Array.Empty<string>(), PageRequest.Of(1, 20));

        page.Items.Should().NotBeNull().And.BeEmpty();
        page.Total.Should().Be(0);
        page.HasMore.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, 1, 20)]        // omitted → defaults
    [InlineData(0, 0, 1, 20)]              // zero/negative collapse to a valid first page
    [InlineData(-3, -9, 1, 20)]            // negatives clamp up
    [InlineData(7, 1000, 7, 100)]          // oversized pageSize capped at MaxPageSize
    [InlineData(2, 50, 2, 50)]             // in-range values pass through
    public void PageRequest_Clamps_Caller_Values_Into_A_Safe_Window(
        int? page, int? pageSize, int expectedPage, int expectedSize)
    {
        var request = PageRequest.Of(page, pageSize);

        request.Page.Should().Be(expectedPage);
        request.PageSize.Should().Be(expectedSize);
        request.Offset.Should().Be((expectedPage - 1) * expectedSize);
    }

    [Fact]
    public void PageRequest_Caps_A_Hostile_PageSize_At_The_Ceiling()
    {
        // SYS-04 safety: ?pageSize=100000 must never become an unbounded fan-out.
        var request = PageRequest.Of(1, 100_000);
        request.PageSize.Should().Be(PageRequest.MaxPageSize);
    }
}
