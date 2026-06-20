namespace JeebGateway.Infrastructure.Pagination;

/// <summary>
/// WS-08 / SYS-04 — the canonical gateway list envelope. Every paginated list
/// surface (requests, offers, notifications, admin lists) should return THIS
/// shape so the client's infinite-scroll machinery is uniform across feeds.
///
/// Field names match the shapes already shipped by <c>Users</c>,
/// <c>ProhibitedItems</c>, and the admin lists (<c>Page</c> / <c>PageSize</c> /
/// <c>Total</c> / <c>Items</c>). The only previously-divergent surface was the
/// notification list (<c>TotalCount</c>); new code adopts this envelope so the
/// drift stops here.
///
/// <see cref="HasMore"/> is DERIVED (never trust a client-supplied flag) and is
/// the single signal the infinite-scroll loader reads to decide whether to fetch
/// the next page — see SYS-04 (D73). It is computed from the page window, not the
/// returned item count, so a short final page still terminates the scroll.
/// </summary>
/// <typeparam name="T">The element type for the current page.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Items for the current page (never null; empty on an empty page).</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>1-based page index of this window.</summary>
    public required int Page { get; init; }

    /// <summary>Page size actually applied (post-clamp), not the raw request.</summary>
    public required int PageSize { get; init; }

    /// <summary>Total item count across all pages for the query.</summary>
    public required int Total { get; init; }

    /// <summary>
    /// True when at least one more page exists after this one. Derived from
    /// <c>Page * PageSize &lt; Total</c> so the client's "load more" affordance
    /// is correct even when the final page is partially filled.
    /// </summary>
    public bool HasMore => (long)Page * PageSize < Total;

    /// <summary>
    /// Builds a canonical page from a materialised window plus the total count.
    /// Use this from list endpoints that already paged at the data layer.
    /// </summary>
    public static PagedResult<T> Create(IReadOnlyList<T> items, PageRequest request, int total) => new()
    {
        Items = items,
        Page = request.Page,
        PageSize = request.PageSize,
        Total = total < 0 ? 0 : total
    };

    /// <summary>
    /// Builds a canonical page by applying the request window to an already-loaded
    /// in-memory sequence. For small/in-memory feeds (the in-memory gateway default
    /// path); large feeds must page at the source and use <see cref="Create"/>.
    /// </summary>
    public static PagedResult<T> From(IEnumerable<T> source, PageRequest request)
    {
        var all = source as IReadOnlyList<T> ?? source.ToList();
        var window = all
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToList();
        return Create(window, request, all.Count);
    }
}
