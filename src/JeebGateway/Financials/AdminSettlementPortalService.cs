using System.Globalization;
using System.Text;
using JeebGateway.Admin;

namespace JeebGateway.Financials;

/// <summary>
/// Admin settlement portal read surface (extracted from PR #364). gwdbx W2-R11: the rows come
/// from settlement-service over the SERVICE scope. Batch reads and mark-paid need the ADMIN
/// scope the gateway deliberately does not hold, so they fail closed — as do dispute/resolve.
/// </summary>
public interface IAdminSettlementPortalService
{
    Task<AdminSettlementPageResponse> ListAsync(
        AdminSettlementPortalListRequest request, CancellationToken ct);

    Task<AdminSettlementDetailResponse?> GetAsync(string settlementId, CancellationToken ct);

    Task<AdminSettlementBatchResponse?> GetBatchAsync(string batchId, CancellationToken ct);

    Task<AdminSettlementMarkPaidResult> MarkBatchPaidAsync(
        string batchId, int expectedVersion, string paymentReference, string reason,
        string adminId, CancellationToken ct);
}

public sealed record AdminSettlementPortalListRequest(
    string? Query,
    string? Status,
    string? ProviderId,
    string? DeliveryId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Sort,
    int Limit,
    string? Cursor);

public enum AdminSettlementMarkPaidOutcome
{
    Ok,
    Replayed,
    NotFound,
    VersionConflict,
}

public sealed record AdminSettlementMarkPaidResult(
    AdminSettlementMarkPaidOutcome Outcome,
    AdminSettlementReconcileResponse? Response);

public sealed class AdminSettlementPortalService : IAdminSettlementPortalService
{
    /// <summary>Portal status vocabulary: intent|pending|batched|paid|disputed|resolved.</summary>
    internal static class PortalStatus
    {
        public const string Intent = "intent";
        public const string Pending = "pending";
        public const string Batched = "batched";
        public const string Paid = "paid";
        public const string Disputed = "disputed";
        public const string Resolved = "resolved";
    }

    private readonly ISettlementServiceClient _settlements;

    public AdminSettlementPortalService(ISettlementServiceClient settlements)
    {
        _settlements = settlements;
    }

    public async Task<AdminSettlementPageResponse> ListAsync(
        AdminSettlementPortalListRequest request, CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, 200);
        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();
        // No dispute owner exists anywhere: disputed/resolved views are always empty.
        if (status is PortalStatus.Disputed or PortalStatus.Resolved)
            return Empty();

        if (status is not null
            and not (PortalStatus.Intent or PortalStatus.Pending or PortalStatus.Batched or PortalStatus.Paid))
            return Empty();

        // Upstream pages by (created_at, id); the portal's cursor is a gateway-side keyset over
        // settled_at. A cursor from a prior page is honoured by filtering, not by re-paging.
        DateTimeOffset? cursorSettledAt = null;
        string? cursorId = null;
        if (!string.IsNullOrWhiteSpace(request.Cursor)
            && !TryDecodeCursor(request.Cursor, out cursorSettledAt, out cursorId))
            return Empty();

        var rows = await _settlements.ListAsync(
            new SettlementListQuery(
                HolderId: string.IsNullOrWhiteSpace(request.ProviderId) ? null : request.ProviderId.Trim(),
                States: null,
                From: request.From,
                To: request.To,
                Limit: 200),
            ct);

        var ascending = string.Equals(request.Sort?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);
        var matched = rows
            .Where(row => status is null || string.Equals(StatusOf(row), status, StringComparison.Ordinal))
            .Where(row => MatchesQuery(row, request));
        var filtered = (ascending
                ? matched.OrderBy(row => row.SettledAt).ThenBy(row => row.Id, StringComparer.Ordinal)
                : matched.OrderByDescending(row => row.SettledAt).ThenByDescending(row => row.Id, StringComparer.Ordinal))
            .ToList();

        if (cursorSettledAt is { } after)
            filtered = filtered
                .Where(row => ascending
                    ? row.SettledAt > after || (row.SettledAt == after && string.CompareOrdinal(row.Id, cursorId) > 0)
                    : row.SettledAt < after || (row.SettledAt == after && string.CompareOrdinal(row.Id, cursorId) < 0))
                .ToList();

        var page = filtered.Take(limit).ToArray();
        var data = page.Select(row => MapSettlement(row, reconciliation: null)).ToArray();
        var nextCursor = page.Length == limit && page.Length > 0
            ? EncodeCursor(page[^1].SettledAt, page[^1].Id)
            : null;
        return new AdminSettlementPageResponse(data, new AdminSettlementPageCursor(nextCursor));
    }

    private static AdminSettlementPageResponse Empty() => new(
        Array.Empty<AdminSettlementResource>(), new AdminSettlementPageCursor(null));

    private static bool MatchesQuery(Settlement row, AdminSettlementPortalListRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DeliveryId)
            && !string.Equals(row.DeliveryId, request.DeliveryId.Trim(), StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(request.Query)) return true;
        var q = request.Query.Trim();
        return row.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.DeliveryId.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.JeeberId.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AdminSettlementDetailResponse?> GetAsync(string settlementId, CancellationToken ct)
    {
        if (!Guid.TryParse(settlementId, out _)) return null;
        var row = await _settlements.GetByIdAsync(settlementId, ct);
        return row is null ? null : new AdminSettlementDetailResponse(MapSettlement(row, reconciliation: null));
    }

    // gwdbx W2-R11: /batches/* is ADMIN scope upstream. The gateway holds the SERVICE token only,
    // by design — a leaked gateway token must not be able to read or pay a payout batch.
    public Task<AdminSettlementBatchResponse?> GetBatchAsync(string batchId, CancellationToken ct)
        => throw new SettlementAdminScopeException(nameof(GetBatchAsync));

    public Task<AdminSettlementMarkPaidResult> MarkBatchPaidAsync(
        string batchId, int expectedVersion, string paymentReference, string reason,
        string adminId, CancellationToken ct)
        => throw new SettlementAdminScopeException(nameof(MarkBatchPaidAsync));

    private static AdminSettlementResource MapSettlement(
        Settlement row, AdminSettlementReconciliation? reconciliation) => new(
        Id: row.Id,
        DeliveryId: row.DeliveryId,
        ProviderId: row.JeeberId,
        Payer: row.ClientId,
        Recipient: new AdminSettlementRecipient("jeeber", row.JeeberId),
        GrossAmount: Money(row.GoodsCost),
        CommissionAmount: Money(row.Commission),
        NetAmount: Money(row.GoodsCost - row.Commission),
        Currency: row.Currency,
        Status: StatusOf(row),
        Version: VersionOf(row),
        SnapshotSequence: null,
        PaidAt: row.PaidAt,
        BatchedAt: row.BatchedAt,
        DisputeReason: null,
        DisputedAt: null,
        DisputedBy: null,
        ResolutionNote: null,
        ResolvedAt: null,
        ResolvedBy: null,
        CreatedAt: row.SettledAt,
        UpdatedAt: row.PaidAt ?? row.BatchedAt ?? row.ReceiptGeneratedAt ?? row.SettledAt,
        BatchId: row.BatchId?.ToString("D"),
        Reconciliation: reconciliation,
        History: null);

    private static string StatusOf(Settlement row) =>
        row.State == SettlementState.PendingSettlement
            ? PortalStatus.Intent
            : row.CodState switch
            {
                CodSettlementState.Batched => PortalStatus.Batched,
                CodSettlementState.Paid => PortalStatus.Paid,
                _ => PortalStatus.Pending,
            };

    /// <summary>Deterministic optimistic-concurrency projection: intent/pending=1, batched=2, paid=3.</summary>
    private static int VersionOf(Settlement row) => StatusOf(row) switch
    {
        PortalStatus.Batched => 2,
        PortalStatus.Paid => 3,
        _ => 1,
    };

    private static string Money(decimal value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);

    private static string EncodeCursor(DateTimeOffset settledAt, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
                settledAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + id))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeCursor(
        string cursor, out DateTimeOffset? settledAt, out string? id)
    {
        settledAt = null;
        id = null;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separator = raw.IndexOf('|');
            if (separator <= 0 || separator == raw.Length - 1) return false;
            if (!DateTimeOffset.TryParse(
                    raw[..separator], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed)) return false;
            settledAt = parsed;
            id = raw[(separator + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>gwdbx W2-R11: the caller asked for a settlement-service ADMIN-scope operation
/// (payout batches, mark-paid). The gateway holds the SERVICE scope only, on purpose.</summary>
public sealed class SettlementAdminScopeException : InvalidOperationException
{
    public const string ProblemType = "https://jeeb.dev/errors/settlement-admin-scope-not-held";

    public SettlementAdminScopeException(string member)
        : base($"'{member}' needs the settlement-service ADMIN scope. The gateway holds the SERVICE "
               + "scope only — payout batches and mark-paid are served by settlement-service directly.")
        => Member = member;

    public string Member { get; }
}
