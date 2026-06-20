namespace JeebGateway.Infrastructure.Pagination;

/// <summary>
/// WS-08 / SYS-04 — a clamped pagination request. Centralises the page/pageSize
/// validation that was previously open-coded per controller (Users clamped to
/// 1..100; AdminProhibitedItems hand-rolled a 1..100 guard; the notification list
/// did neither and forwarded a raw <c>pageSize</c> upstream). Constructing through
/// <see cref="Of"/> guarantees a non-negative, bounded window so a hostile or
/// fat-fingered <c>?pageSize=100000</c> can never translate into an unbounded
/// upstream fan-out or an OOM materialisation.
/// </summary>
public sealed class PageRequest
{
    /// <summary>Org-wide default page size when the caller omits one.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Hard ceiling — matches the existing Users/AdminProhibitedItems cap.</summary>
    public const int MaxPageSize = 100;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>1-based, clamped to a minimum of 1.</summary>
    public int Page { get; }

    /// <summary>Clamped to 1..<see cref="MaxPageSize"/>.</summary>
    public int PageSize { get; }

    /// <summary>Zero-based offset for data-layer SKIP, derived from the clamped window.</summary>
    public int Offset => (Page - 1) * PageSize;

    /// <summary>
    /// Clamps caller-supplied query values into a safe window. Null or out-of-range
    /// inputs collapse to defaults rather than erroring — list reads should degrade
    /// to a valid first page, not 400, so an empty/garbage scroll cursor never
    /// breaks a feed (SYS-02 empty/error envelopes).
    /// </summary>
    public static PageRequest Of(int? page, int? pageSize)
    {
        var p = page is null or < 1 ? 1 : page.Value;
        var size = pageSize switch
        {
            null or < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize.Value
        };
        return new PageRequest(p, size);
    }
}
