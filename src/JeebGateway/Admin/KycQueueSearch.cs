using JeebGateway.Kyc;
using JeebGateway.Users;

namespace JeebGateway.Admin;

/// <summary>
/// The ONE gateway-side KYC queue search, shared by <c>GET /admin/kyc/queue?q=</c> and the
/// CMS-compat <c>GET /user-management/admin/kyc?q=</c> so the two can never diverge.
///
/// <para><b>Why filtering lives here and not in kyc-service.</b> kyc-service's list API takes
/// only <c>status/page/pageSize</c>, and its rows are product-agnostic — a subject id, no name
/// and no phone. There is nothing downstream to search ON. The gateway owns the user projection
/// (<see cref="IUsersStore"/> Name/Phone), so the join and the substring match happen here.</para>
///
/// <para><b>Honest bound.</b> A <c>q</c> search pulls at most
/// <see cref="MaxWindowPages"/> × <see cref="WindowPageSize"/> pending rows
/// (<see cref="MaxWindowRows"/>) and filters those. A larger pending queue is truncated, not
/// silently mis-reported: <see cref="KycQueueSearchPage.WindowTruncated"/> says so.</para>
/// </summary>
public sealed class KycQueueSearch
{
    /// <summary>Seam page size used while walking the pending queue for a q search.</summary>
    public const int WindowPageSize = 100;

    /// <summary>Maximum seam pages walked for a q search.</summary>
    public const int MaxWindowPages = 5;

    /// <summary>Maximum pending rows a q search can consider.</summary>
    public const int MaxWindowRows = WindowPageSize * MaxWindowPages;

    private readonly IKycBffSeam _kyc;
    private readonly IUsersStore _users;
    private readonly ILogger<KycQueueSearch> _log;

    public KycQueueSearch(IKycBffSeam kyc, IUsersStore users, ILogger<KycQueueSearch> log)
    {
        _kyc = kyc;
        _users = users;
        _log = log;
    }

    /// <summary>
    /// Pending queue page, user-decorated, optionally filtered by a case-insensitive
    /// name/phone substring. Throws <see cref="KycUpstreamDisabledException"/> from the seam —
    /// callers map it to the 503 they already declare.
    /// </summary>
    public async Task<KycQueueSearchPage> SearchAsync(
        int page, int pageSize, string? q, CancellationToken ct)
    {
        var needle = q?.Trim();

        if (string.IsNullOrEmpty(needle))
        {
            var queue = await _kyc.GetPendingQueueAsync(page, pageSize, ct);
            var decorated = await DecorateAsync(queue.Items, ct);
            return new KycQueueSearchPage(decorated, queue.Page, queue.PageSize, queue.Total, false);
        }

        var window = new List<KycBffQueueItem>();
        var truncated = false;
        for (var p = 1; p <= MaxWindowPages; p++)
        {
            var slice = await _kyc.GetPendingQueueAsync(p, WindowPageSize, ct);
            window.AddRange(slice.Items);
            if (slice.Items.Count < WindowPageSize) break;
            if (p == MaxWindowPages && window.Count < slice.Total) truncated = true;
        }

        var rows = await DecorateAsync(window, ct);
        var matches = rows.Where(r => Matches(r, needle)).ToList();
        var slicePage = matches.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new KycQueueSearchPage(slicePage, page, pageSize, matches.Count, truncated);
    }

    private static bool Matches(KycQueueSearchRow row, string needle)
        => (row.UserName is not null && row.UserName.Contains(needle, StringComparison.OrdinalIgnoreCase))
           || (row.Phone is not null && row.Phone.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Joins each queue row to the gateway user projection. Fail-soft: an unresolvable
    /// user leaves name/phone null (the row still lists) rather than failing the page.
    /// </summary>
    private async Task<IReadOnlyList<KycQueueSearchRow>> DecorateAsync(
        IReadOnlyList<KycBffQueueItem> items, CancellationToken ct)
    {
        var cache = new Dictionary<string, UserProfile?>(StringComparer.Ordinal);
        var rows = new List<KycQueueSearchRow>(items.Count);

        foreach (var item in items)
        {
            UserProfile? profile = null;
            if (!string.IsNullOrWhiteSpace(item.UserId))
            {
                if (!cache.TryGetValue(item.UserId, out profile))
                {
                    try
                    {
                        profile = await _users.GetByIdAsync(item.UserId, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "kyc queue: user projection lookup failed for {UserId}; row listed undecorated",
                            item.UserId);
                    }
                    cache[item.UserId] = profile;
                }
            }

            rows.Add(new KycQueueSearchRow(
                item,
                string.IsNullOrWhiteSpace(profile?.Name) ? null : profile!.Name,
                string.IsNullOrWhiteSpace(profile?.Phone) ? null : profile!.Phone));
        }

        return rows;
    }
}

/// <summary>One pending-queue row joined to the gateway's user projection.</summary>
public sealed record KycQueueSearchRow(KycBffQueueItem Item, string? UserName, string? Phone);

/// <summary>
/// A page of user-decorated queue rows. <paramref name="Total"/> is the seam total when no
/// filter ran, and the FILTERED count when it did.
/// </summary>
public sealed record KycQueueSearchPage(
    IReadOnlyList<KycQueueSearchRow> Items,
    int Page,
    int PageSize,
    int Total,
    bool WindowTruncated);
