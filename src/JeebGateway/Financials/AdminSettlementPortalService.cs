using System.Globalization;
using System.Text;
using JeebGateway.Admin;

namespace JeebGateway.Financials;

/// <summary>
/// In-gateway COD implementation of the admin settlement portal read/reconcile
/// surface (extracted from PR #364, re-homed off the retired UPG proxy). Serves
/// the exact wire shapes in <see cref="AdminSettlementResource"/> and friends
/// from the gateway's own settlement owner (<see cref="ISettlementStore"/> +
/// <see cref="ISettlementBatchStore"/>). Jeeb is cash on delivery: automated
/// dispute/resolve mutations have no local owner columns yet and fail closed.
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

    private readonly ISettlementStore _settlements;
    private readonly ISettlementBatchStore _batches;
    private readonly TimeProvider _clock;
    private readonly ILogger<AdminSettlementPortalService> _log;

    public AdminSettlementPortalService(
        ISettlementStore settlements,
        ISettlementBatchStore batches,
        TimeProvider clock,
        ILogger<AdminSettlementPortalService> log)
    {
        _settlements = settlements;
        _batches = batches;
        _clock = clock;
        _log = log;
    }

    public async Task<AdminSettlementPageResponse> ListAsync(
        AdminSettlementPortalListRequest request, CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, 200);
        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();
        // No local dispute owner exists: disputed/resolved views are always empty.
        if (status is PortalStatus.Disputed or PortalStatus.Resolved)
            return new AdminSettlementPageResponse(
                Array.Empty<AdminSettlementResource>(), new AdminSettlementPageCursor(null));

        string? state = null;
        string? codState = null;
        var excludeIntent = false;
        switch (status)
        {
            case null:
                break;
            case PortalStatus.Intent:
                state = SettlementState.PendingSettlement;
                break;
            case PortalStatus.Pending:
                codState = CodSettlementState.Recorded;
                excludeIntent = true;
                break;
            case PortalStatus.Batched:
                codState = CodSettlementState.Batched;
                break;
            case PortalStatus.Paid:
                codState = CodSettlementState.Paid;
                break;
            default:
                return new AdminSettlementPageResponse(
                    Array.Empty<AdminSettlementResource>(), new AdminSettlementPageCursor(null));
        }

        var ascending = string.Equals(request.Sort?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);
        DateTimeOffset? cursorSettledAt = null;
        string? cursorId = null;
        if (!string.IsNullOrWhiteSpace(request.Cursor)
            && !TryDecodeCursor(request.Cursor, out cursorSettledAt, out cursorId))
            return new AdminSettlementPageResponse(
                Array.Empty<AdminSettlementResource>(), new AdminSettlementPageCursor(null));

        var rows = await _settlements.ListPageForAdminAsync(
            new AdminSettlementPortalFilter(
                Query: string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim(),
                JeeberId: string.IsNullOrWhiteSpace(request.ProviderId) ? null : request.ProviderId.Trim(),
                DeliveryId: string.IsNullOrWhiteSpace(request.DeliveryId) ? null : request.DeliveryId.Trim(),
                State: state,
                CodState: codState,
                ExcludeIntent: excludeIntent,
                From: request.From,
                To: request.To,
                Ascending: ascending,
                CursorSettledAt: cursorSettledAt,
                CursorId: cursorId),
            limit,
            ct);

        var data = rows.Select(row => MapSettlement(row, reconciliation: null)).ToArray();
        var nextCursor = rows.Count == limit
            ? EncodeCursor(rows[^1].SettledAt, rows[^1].Id)
            : null;
        return new AdminSettlementPageResponse(data, new AdminSettlementPageCursor(nextCursor));
    }

    public async Task<AdminSettlementDetailResponse?> GetAsync(string settlementId, CancellationToken ct)
    {
        if (!Guid.TryParse(settlementId, out _)) return null;
        var row = await _settlements.GetByIdAsync(settlementId, ct);
        if (row is null) return null;

        AdminSettlementReconciliation? reconciliation = null;
        if (row.BatchId is Guid batchId)
        {
            var batch = await _batches.GetByIdAsync(batchId, ct);
            if (batch is not null)
                reconciliation = new AdminSettlementReconciliation(
                    BatchId: batch.Id.ToString("D"),
                    BatchStatus: batch.Status,
                    Version: BatchVersion(batch),
                    PaymentReference: null,
                    Note: null,
                    PaidBy: batch.PaidBy,
                    AllowedActions: string.Equals(batch.Status, "paid", StringComparison.Ordinal)
                        ? Array.Empty<string>()
                        : new[] { "mark-paid" });
        }

        return new AdminSettlementDetailResponse(MapSettlement(row, reconciliation));
    }

    public async Task<AdminSettlementBatchResponse?> GetBatchAsync(string batchId, CancellationToken ct)
    {
        if (!Guid.TryParse(batchId, out var id)) return null;
        var batch = await _batches.GetByIdAsync(id, ct);
        if (batch is null) return null;
        var settlements = await ListBatchSettlementsAsync(batch, ct);
        return new AdminSettlementBatchResponse(MapBatch(batch, settlements));
    }

    public async Task<AdminSettlementMarkPaidResult> MarkBatchPaidAsync(
        string batchId, int expectedVersion, string paymentReference, string reason,
        string adminId, CancellationToken ct)
    {
        if (!Guid.TryParse(batchId, out var id))
            return new AdminSettlementMarkPaidResult(AdminSettlementMarkPaidOutcome.NotFound, null);
        var batch = await _batches.GetByIdAsync(id, ct);
        if (batch is null)
            return new AdminSettlementMarkPaidResult(AdminSettlementMarkPaidOutcome.NotFound, null);

        if (string.Equals(batch.Status, "paid", StringComparison.Ordinal))
        {
            var replayedSettlements = await ListBatchSettlementsAsync(batch, ct);
            return new AdminSettlementMarkPaidResult(
                AdminSettlementMarkPaidOutcome.Replayed,
                new AdminSettlementReconcileResponse(
                    MapBatch(batch, replayedSettlements),
                    SettlementsUpdated: 0,
                    NotificationId: "not-dispatched"));
        }

        if (expectedVersion != BatchVersion(batch))
            return new AdminSettlementMarkPaidResult(AdminSettlementMarkPaidOutcome.VersionConflict, null);

        var paidAt = _clock.GetUtcNow();
        var paid = await _batches.MarkPaidAsync(id, adminId, paidAt, ct);
        // Audit trail: the local owner has no payment_reference column yet; the
        // structured log is the authoritative record of the operator's evidence.
        _log.LogInformation(
            "Admin settlement batch {BatchId} marked paid by {AdminId}; paymentReference={PaymentReference} reason={Reason}",
            id, adminId, paymentReference, reason);
        var settlements = await ListBatchSettlementsAsync(paid, ct);
        return new AdminSettlementMarkPaidResult(
            AdminSettlementMarkPaidOutcome.Ok,
            new AdminSettlementReconcileResponse(
                MapBatch(paid, settlements),
                SettlementsUpdated: paid.SettlementCount,
                NotificationId: "not-dispatched"));
    }

    private async Task<IReadOnlyList<Settlement>> ListBatchSettlementsAsync(
        SettlementBatch batch, CancellationToken ct)
    {
        var from = new DateTimeOffset(
            batch.PeriodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var to = new DateTimeOffset(
            batch.PeriodEnd.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero).AddDays(1);
        var window = await _settlements.ListByJeeberAsync(batch.JeeberId, from, to, ct);
        return window.Where(row => row.BatchId == batch.Id).ToArray();
    }

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

    private AdminSettlementBatchResource MapBatch(
        SettlementBatch batch, IReadOnlyList<Settlement> settlements) => new(
        Id: batch.Id.ToString("D"),
        ProviderId: batch.JeeberId,
        TotalNetUsd: Money(batch.TotalNetUsd),
        Currency: batch.Currency,
        Status: batch.Status,
        PeriodStart: batch.PeriodStart,
        PeriodEnd: batch.PeriodEnd,
        SettlementCount: batch.SettlementCount,
        PaidAt: batch.PaidAt,
        PaidBy: batch.PaidBy,
        Version: BatchVersion(batch),
        PaymentReference: null,
        ReconciliationNote: null,
        Metadata: null,
        CreatedAt: batch.CreatedAt,
        UpdatedAt: batch.UpdatedAt,
        Settlements: settlements
            .Select(row => MapSettlement(row, reconciliation: null))
            .ToArray());

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

    internal static int BatchVersion(SettlementBatch batch) =>
        string.Equals(batch.Status, "paid", StringComparison.Ordinal) ? 2 : 1;

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
