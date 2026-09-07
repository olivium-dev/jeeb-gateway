using System;
using System.Collections.Generic;
using System.Linq;
using JeebGateway.Notifications;

namespace JeebGateway.JeebNotifications;

/// <summary>
/// The generic→Jeeb projection for the notifications inbox the mobile app consumes
/// (<c>DioNotificationsRepository</c>, JM-057):
///
/// <list type="bullet">
///   <item><c>GET /v1/notifications?userId=</c> — the user's inbox page.</item>
///   <item><c>PATCH /v1/notifications/{id}/read</c> — mark one read (no body to project).</item>
/// </list>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): every method here is a pure, side-effect-free
/// shaping of the generic, product-agnostic notification-service receiver-list
/// primitive into the Jeeb-facing mobile contract — no state, no persistence, no
/// domain rules. It is the sibling of <see cref="JeebGateway.JeebReviews.JeebReviewsProjection"/>
/// / <see cref="JeebGateway.JeebWallet.JeebWalletProjection"/> and is unit-tested without HTTP/DI.
/// </para>
///
/// <para>
/// Coverage note: the generic <see cref="JeebGateway.service.ServiceNotification.ServiceNotificationClient"/>
/// DOES expose the receiver list (<c>Get_messages_by_receiver…</c>) and a single
/// mark-read (<c>Mark_notification_read…</c>), so these routes are wired to the real
/// primitive — no fabricated state. An explicitly empty list produces an empty page;
/// absent or malformed upstream data is a contract failure.
/// </para>
/// </summary>
public static class JeebNotificationsProjection
{
    /// <summary>The upstream status value that means a row has been read (case-insensitive).</summary>
    private const string ReadStatus = "read";

    /// <summary>
    /// Project the generic receiver-list rows into the mobile inbox envelope. Pure:
    /// the controller extracts <paramref name="rows"/> from the (Newtonsoft) upstream
    /// payload and this method shapes them — newest-first ordering is preserved from
    /// upstream. An empty <paramref name="rows"/> yields the cold-start empty page.
    /// </summary>
    public static JeebNotificationsPageResponse ProjectPage(
        IEnumerable<UpstreamNotificationRow>? rows,
        int page,
        int pageSize,
        int? upstreamTotal = null)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;

        if (rows is null) throw new NotificationContractException();
        var items = rows
            .Select(ProjectItem)
            .ToList();

        if (upstreamTotal is < 0 || upstreamTotal < items.Count)
            throw new NotificationContractException();
        var total = upstreamTotal ?? items.Count;
        var totalPages = total <= 0 ? 1 : (int)Math.Ceiling(total / (double)safeSize);

        return new JeebNotificationsPageResponse
        {
            Items = items,
            Page = safePage,
            PageSize = safeSize,
            TotalCount = total,
            TotalPages = totalPages < 1 ? 1 : totalPages,
        };
    }

    /// <summary>
    /// Project one normalized upstream row into the mobile <c>{ id, type, title, body,
    /// ts, read, ref }</c> shape. The upstream <c>status</c> string is reduced to the
    /// boolean <c>read</c> the mobile parser expects; the opaque <c>type</c> passes
    /// through (the mobile <c>_kind</c> switch maps known values, others → unknown).
    /// </summary>
    public static JeebNotificationItemResponse ProjectItem(UpstreamNotificationRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        return new JeebNotificationItemResponse
        {
            Id = row.Id ?? string.Empty,
            Type = NullIfBlank(row.Type),
            Title = row.Title ?? string.Empty,
            Body = row.Body ?? string.Empty,
            Ts = row.Timestamp ?? string.Empty,
            Read = IsRead(row.Status),
            Ref = NotificationDeepLinkResolver.ValidateEntityId(row.Ref),
            DeepLink = ExplicitRoute(row.DeepLink)
                ?? NotificationDeepLinkResolver.Resolve(row.Type, row.Ref),
        };
    }

    // A stored link outside the route grammar is no route for this row: the type resolves it
    // (inbox root when the type has none). A malformed link is still a contract breach.
    private static string? ExplicitRoute(string? storedLink)
        => storedLink is null ? null : NotificationDeepLinkResolver.MatchExplicitLink(storedLink);

    /// <summary>Upstream <c>status == "read"</c> (case-insensitive) ⇒ the row is read.</summary>
    public static bool IsRead(string? status)
        => string.Equals(status?.Trim(), ReadStatus, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
