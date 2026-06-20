using System;
using System.Collections.Generic;
using System.Linq;

namespace JeebGateway.JeebSupport;

/// <summary>
/// The pure, generic→Jeeb projection for the support-ticket surface the mobile
/// app consumes (<c>DioSupportRepository</c>, JM-063):
///
/// <list type="bullet">
///   <item><c>POST /v1/support/tickets</c> — create (draft → stored row → mobile DTO).</item>
///   <item><c>GET  /v1/support/tickets/{id}</c> — read one (row → DTO).</item>
///   <item><c>GET  /v1/support/tickets</c> — list the caller's tickets (rows → page).</item>
///   <item><c>GET  /v1/support/categories</c> — static gateway-owned catalog (no state).</item>
/// </list>
///
/// <para>
/// ADR-0001/0005 (STATELESS &amp; THIN): every method here is a pure, side-effect-free
/// shaping — the controller persists/reads the opaque rows via jeeb-state-service
/// (<c>IIdempotencyStore</c> KV, ADR-0005) and this maps them. No state, no
/// persistence, no domain rules live here. Sibling of
/// <see cref="JeebGateway.JeebNotifications.JeebNotificationsProjection"/>; unit-tested
/// without HTTP/DI.
/// </para>
///
/// <para><b>DTO-drift reconciliation (mobile-facing shape authoritative).</b>
/// Two mobile↔canonical drifts are fixed HERE so mobile's live payload is accepted:
/// (1) the category enum — mobile sends <c>delivery</c>/<c>kycAppeal</c>, the canonical
/// catalog spells those <c>order</c>/<c>kyc</c>; (2) the order link — mobile sends
/// <c>orderRef</c>, the canonical row names it <c>orderId</c>. Both are normalized to the
/// canonical form on create.</para>
/// </summary>
public static class JeebSupportProjection
{
    /// <summary>
    /// The canonical gateway-owned category catalog (id → picker label). Static — this is
    /// a constant, NOT state. Ids are what the create route stores and echoes back.
    /// </summary>
    public static readonly IReadOnlyList<SupportCategoryResponse> Categories = new[]
    {
        new SupportCategoryResponse { Id = "order", Label = "Order / delivery" },
        new SupportCategoryResponse { Id = "payment", Label = "Payment" },
        new SupportCategoryResponse { Id = "account", Label = "Account" },
        new SupportCategoryResponse { Id = "kyc", Label = "KYC / verification" },
        new SupportCategoryResponse { Id = "dispute", Label = "Dispute" },
        new SupportCategoryResponse { Id = "other", Label = "Other" },
    };

    private static readonly HashSet<string> CanonicalCategories =
        new(Categories.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reconcile a client-supplied category name to a canonical catalog id. Accepts the
    /// canonical ids directly AND the mobile enum names that drift from them
    /// (<c>delivery</c>→<c>order</c>, <c>kycAppeal</c>→<c>kyc</c>, case-insensitive). Returns
    /// <c>null</c> for an unknown/blank category so the controller can 400 it.
    /// </summary>
    public static string? ReconcileCategory(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        // Mobile enum-name drift → canonical catalog id.
        switch (value.ToLowerInvariant())
        {
            case "delivery": return "order";
            case "kycappeal": return "kyc";
        }

        return CanonicalCategories.TryGetValue(value, out var canonical) ? canonical : null;
    }

    /// <summary>True when <paramref name="category"/> resolves to a known catalog id.</summary>
    public static bool IsKnownCategory(string? category) => ReconcileCategory(category) is not null;

    /// <summary>
    /// Build a new stored ticket row from the validated create request. The id is supplied
    /// by the controller (a server-minted GUID = the opaque KV key); the ticketNumber is the
    /// <c>SUP-&lt;last6 of epoch ms&gt;</c> form the mock contract uses. Order link is the
    /// reconciled <c>orderId</c> (mobile <c>orderRef</c> wins if only it is set).
    /// </summary>
    public static SupportTicketRow BuildRow(
        string id,
        string ownerId,
        CreateSupportTicketRequest request,
        DateTimeOffset now)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var category = ReconcileCategory(request.Category)
            ?? throw new ArgumentException("Unknown support category.", nameof(request));

        var iso = now.ToUniversalTime().ToString("o");
        var orderId = NullIfBlank(request.OrderId) ?? NullIfBlank(request.OrderRef);

        return new SupportTicketRow
        {
            Id = id,
            TicketNumber = MintTicketNumber(now),
            UserId = ownerId,
            Category = category,
            Subject = NullIfBlank(request.Subject),
            Body = request.Body?.Trim() ?? string.Empty,
            Attachments = (request.Attachments ?? Array.Empty<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToList(),
            OrderId = orderId,
            Status = "open",
            CreatedAt = iso,
            UpdatedAt = iso,
        };
    }

    /// <summary><c>SUP-&lt;last 6 digits of epoch milliseconds&gt;</c> (mock contract shape).</summary>
    public static string MintTicketNumber(DateTimeOffset now)
    {
        var epochMs = now.ToUnixTimeMilliseconds().ToString();
        var last6 = epochMs.Length <= 6 ? epochMs.PadLeft(6, '0') : epochMs[^6..];
        return $"SUP-{last6}";
    }

    /// <summary>Project one stored row into the mobile-facing ticket DTO.</summary>
    public static SupportTicketResponse ProjectTicket(SupportTicketRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        return new SupportTicketResponse
        {
            Id = row.Id ?? string.Empty,
            TicketNumber = row.TicketNumber ?? string.Empty,
            UserId = row.UserId ?? string.Empty,
            Category = row.Category ?? string.Empty,
            Subject = NullIfBlank(row.Subject),
            Body = row.Body ?? string.Empty,
            Attachments = row.Attachments ?? Array.Empty<string>(),
            OrderId = NullIfBlank(row.OrderId),
            Status = string.IsNullOrWhiteSpace(row.Status) ? "open" : row.Status,
            CreatedAt = row.CreatedAt ?? string.Empty,
            UpdatedAt = row.UpdatedAt ?? string.Empty,
        };
    }

    /// <summary>
    /// Project the caller's stored rows into the paged list envelope, newest-first. Pure: the
    /// controller reads <paramref name="rows"/> from jeeb-state-service and this shapes +
    /// pages them. A null/empty <paramref name="rows"/> yields the cold-start empty page the
    /// mobile parser tolerates.
    /// </summary>
    public static SupportTicketsPageResponse ProjectPage(
        IEnumerable<SupportTicketRow>? rows,
        int page,
        int pageSize)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : (pageSize > 100 ? 100 : pageSize);

        var ordered = (rows ?? Array.Empty<SupportTicketRow>())
            .Where(r => r is not null)
            .OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal)
            .ToList();

        var total = ordered.Count;
        var totalPages = total <= 0 ? 1 : (int)Math.Ceiling(total / (double)safeSize);

        var items = ordered
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(ProjectTicket)
            .ToList();

        return new SupportTicketsPageResponse
        {
            Items = items,
            Page = safePage,
            PageSize = safeSize,
            TotalCount = total,
            TotalPages = totalPages < 1 ? 1 : totalPages,
            Cursor = null,
        };
    }

    /// <summary>The static categories catalog envelope (gateway-owned; no upstream call).</summary>
    public static SupportCategoriesResponse ProjectCategories()
        => new() { Items = Categories };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
