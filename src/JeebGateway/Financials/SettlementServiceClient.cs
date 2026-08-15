using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Financials;

/// <summary>Binds Services:Settlement. ApiToken is the SERVICE scope only — the admin
/// scope (batches / mark-paid / diagnostics) is deliberately never held by the gateway.</summary>
public sealed class SettlementServiceOptions
{
    public const string SectionName = "Services:Settlement";

    public string? BaseUrl { get; set; }

    public string? ApiToken { get; set; }
}

/// <summary>gwdbx W2-R11: settlement-service is unreachable or refused the call. Mapped to a
/// typed 503 so a caller sees "unavailable", never a confident empty (O10).</summary>
public sealed class SettlementServiceUnavailableException : Exception
{
    public const string ProblemType = "https://jeeb.dev/errors/settlement-service-unavailable";

    public SettlementServiceUnavailableException(string member, string detail, Exception? inner = null)
        : base($"settlement-service call '{member}' failed: {detail}", inner) => Member = member;

    public string Member { get; }
}

/// <summary>One settle command. Omit <see cref="GrossAmount"/> to record a pending intent —
/// upstream stores money as NULL, not 0, on a pending row.</summary>
public sealed record SettlementSettleCommand(
    string DeliveryId,
    string HolderId,
    string ClientId,
    string? TierId,
    decimal? GrossAmount,
    string PaymentMethod,
    DateTimeOffset? SettledAt = null);

/// <summary>Settle outcome. <see cref="Created"/> distinguishes a fresh row (201) from a
/// promotion/replay (200); <see cref="HolderExcluded"/> is the non-GUID holder rule (A21 §6 / D7).</summary>
public sealed record SettlementSettleResult(Settlement? Row, bool Created, bool HolderExcluded = false);

/// <summary>Bounded list filter over GET /settlements.</summary>
public sealed record SettlementListQuery(
    string? HolderId = null,
    IReadOnlyCollection<string>? States = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 200);

/// <summary>
/// gwdbx W2-R11: the gateway's ONLY settlement surface. Replaces the deleted local
/// ISettlementStore / ISettlementBatchStore / ISettlementLedgerClient trio — settlement-service
/// (olivium-dev/settlement-service) owns the rows, the commission arithmetic and the ledger.
/// Routes are UNVERSIONED (A21 §4) and carry the SERVICE-scope bearer token.
/// </summary>
public interface ISettlementServiceClient
{
    /// <summary>POST /settlements. Idempotent on delivery id: a duplicate settle returns the
    /// stored row rather than creating a second one.</summary>
    Task<SettlementSettleResult> SettleAsync(SettlementSettleCommand command, CancellationToken ct);

    Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct);

    Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct);

    Task<IReadOnlyList<Settlement>> ListAsync(SettlementListQuery query, CancellationToken ct);

    /// <summary>POST /settlements/{id}/receipt — first-write-wins stamp; a replay returns the
    /// original instant.</summary>
    Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, CancellationToken ct);

    /// <summary>GET /earnings/summary — the platform-wide / per-holder net total.</summary>
    Task<decimal> SumNetEarningsAsync(
        string? holderId,
        IReadOnlyCollection<string>? states,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct);
}

public sealed class SettlementServiceClient : ISettlementServiceClient
{
    public const string HttpClientName = "settlement-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<SettlementServiceClient> _log;

    public SettlementServiceClient(HttpClient http, ILogger<SettlementServiceClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<SettlementSettleResult> SettleAsync(
        SettlementSettleCommand command, CancellationToken ct)
    {
        // Non-GUID holder ids are excluded upstream by rule, not repaired. Skip-and-log is the
        // WalletApiSettlementLedgerClient precedent — never turn it into a settle attempt.
        if (!Guid.TryParse(command.HolderId, out _))
        {
            _log.LogWarning(
                "settlement.settle SKIP delivery {DeliveryId}: holderId {HolderId} is not a GUID; "
                + "formally excluded from settlement (A21 §6 / D7).",
                command.DeliveryId, command.HolderId);
            return new SettlementSettleResult(null, Created: false, HolderExcluded: true);
        }

        var body = new SettleWire
        {
            DeliveryId = command.DeliveryId,
            HolderId = command.HolderId,
            ClientId = command.ClientId,
            TierId = command.TierId ?? string.Empty,
            GrossAmount = command.GrossAmount,
            Currency = SettlementService.CurrencyUsd,
            PaymentMethod = command.PaymentMethod,
            SettledAt = command.SettledAt,
        };

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "settlements")
            {
                Content = JsonContent.Create(body, options: Json),
            },
            nameof(SettleAsync), ct);

        // 409 conflicting-amount: the stored money stands and is never overwritten. Re-read it so
        // the caller gets the authoritative row instead of the two-sided conflict envelope.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _log.LogWarning(
                "settlement.settle CONFLICT delivery {DeliveryId}: stored settlement has different "
                + "money; the stored row stands.", command.DeliveryId);
            var stored = await GetByDeliveryAsync(command.DeliveryId, ct);
            return new SettlementSettleResult(stored, Created: false);
        }

        await EnsureSuccessAsync(response, nameof(SettleAsync), ct);
        var row = await ReadAsync(response, nameof(SettleAsync), ct);

        // 201 Created and 200 Promoted both CHANGED the row; only an explicitly replayed 200 did
        // not. Treating a promotion as a replay would skip the earnings-cache eviction on a real credit.
        var replayed = response.Headers.TryGetValues("Idempotency-Replayed", out var flag)
            && flag.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        return new SettlementSettleResult(row, Created: !replayed);
    }

    public async Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get, $"settlements/by-delivery/{Uri.EscapeDataString(deliveryId)}"),
            nameof(GetByDeliveryAsync), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, nameof(GetByDeliveryAsync), ct);
        return await ReadAsync(response, nameof(GetByDeliveryAsync), ct);
    }

    public async Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
    {
        if (!Guid.TryParse(settlementId, out var id)) return null;

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"settlements/{id:D}"),
            nameof(GetByIdAsync), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, nameof(GetByIdAsync), ct);
        return await ReadAsync(response, nameof(GetByIdAsync), ct);
    }

    public async Task<IReadOnlyList<Settlement>> ListAsync(
        SettlementListQuery query, CancellationToken ct)
    {
        var url = "settlements?limit=" + Math.Clamp(query.Limit, 1, 200);
        if (!string.IsNullOrWhiteSpace(query.HolderId))
            url += "&holderId=" + Uri.EscapeDataString(query.HolderId);
        foreach (var state in MapStates(query.States))
            url += "&state=" + Uri.EscapeDataString(state);
        if (query.From is { } from) url += "&from=" + Uri.EscapeDataString(from.ToString("O"));
        if (query.To is { } to) url += "&to=" + Uri.EscapeDataString(to.ToString("O"));

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), nameof(ListAsync), ct);
        await EnsureSuccessAsync(response, nameof(ListAsync), ct);

        // Oldest-first, matching the deleted ListByJeeberAsync contract the earnings
        // projection depends on (its period start is the first row's settled_at).
        var page = await ReadJsonAsync<SettlementPageWire>(response, nameof(ListAsync), ct);
        return (page?.Items ?? []).Select(Map).OrderBy(s => s.SettledAt).ToArray();
    }

    public async Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, CancellationToken ct)
    {
        if (!Guid.TryParse(settlementId, out var id)) return null;

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"settlements/{id:D}/receipt"),
            nameof(MarkReceiptGeneratedAsync), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, nameof(MarkReceiptGeneratedAsync), ct);
        return await ReadAsync(response, nameof(MarkReceiptGeneratedAsync), ct);
    }

    public async Task<decimal> SumNetEarningsAsync(
        string? holderId,
        IReadOnlyCollection<string>? states,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct)
    {
        var url = "earnings/summary?currency=" + SettlementService.CurrencyUsd;
        if (!string.IsNullOrWhiteSpace(holderId))
            url += "&holderId=" + Uri.EscapeDataString(holderId);
        foreach (var state in MapStates(states))
            url += "&state=" + Uri.EscapeDataString(state);
        if (from is { } f) url += "&from=" + Uri.EscapeDataString(f.ToString("O"));
        if (to is { } t) url += "&to=" + Uri.EscapeDataString(t.ToString("O"));

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), nameof(SumNetEarningsAsync), ct);
        await EnsureSuccessAsync(response, nameof(SumNetEarningsAsync), ct);

        var summary = await ReadJsonAsync<EarningsSummaryWire>(response, nameof(SumNetEarningsAsync), ct);
        return summary?.NetTotal ?? 0m;
    }

    // ── vocabulary translation ────────────────────────────────────────────────

    /// <summary>Gateway COD states (recorded|batched|paid) → upstream states
    /// (settled|batched|paid). "recorded" is unknown upstream and would 400.</summary>
    internal static IReadOnlyList<string> MapStates(IReadOnlyCollection<string>? codStates)
    {
        if (codStates is null || codStates.Count == 0) return [];
        return codStates
            .Select(s => string.Equals(s, CodSettlementState.Recorded, StringComparison.Ordinal)
                ? UpstreamState.Settled
                : s)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static class UpstreamState
    {
        public const string Pending = "pending";
        public const string Settled = "settled";
        public const string Batched = "batched";
        public const string Paid = "paid";
    }

    /// <summary>Projects the upstream row onto the gateway's <see cref="Settlement"/> shape so
    /// every existing wire response (receipt, intent, earnings, admin portal) is unchanged.</summary>
    internal static Settlement Map(SettlementWire w)
    {
        var gross = w.GrossAmount ?? 0m;
        var commission = w.CommissionAmount ?? 0m;
        var pending = string.Equals(w.State, UpstreamState.Pending, StringComparison.Ordinal);
        return new Settlement
        {
            Id = w.SettlementId.ToString(),
            DeliveryId = w.DeliveryId,
            ClientId = w.ClientId,
            JeeberId = w.HolderId,
            TierId = w.TierId,
            GoodsCost = gross,
            CommissionTier = CommissionCalculator.ResolveTier(w.TierId),
            CommissionRate = w.CommissionRate ?? 0m,
            Commission = commission,
            Insurance = 0m,
            Total = commission,
            MinimumFeeApplied = false,
            Currency = w.Currency,
            PaymentMethod = w.PaymentMethod,
            State = pending
                ? SettlementState.PendingSettlement
                : w.ReceiptGeneratedAt is null
                    ? SettlementState.Settled
                    : SettlementState.ReceiptGenerated,
            SettledAt = w.SettledAt ?? w.CreatedAt,
            ReceiptGeneratedAt = w.ReceiptGeneratedAt,
            BatchId = w.BatchId,
            BatchedAt = w.BatchedAt,
            PaidAt = w.PaidAt,
            CodState = w.State switch
            {
                UpstreamState.Batched => CodSettlementState.Batched,
                UpstreamState.Paid => CodSettlementState.Paid,
                _ => CodSettlementState.Recorded,
            },
        };
    }

    // ── transport ─────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> request, string member, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SettlementServiceUnavailableException(member, "transport fault", ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string member, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new SettlementServiceUnavailableException(
            member, $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
    }

    private static async Task<Settlement?> ReadAsync(
        HttpResponseMessage response, string member, CancellationToken ct)
    {
        var wire = await ReadJsonAsync<SettlementWire>(response, member, ct);
        return wire is null ? null : Map(wire);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response, string member, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (JsonException ex)
        {
            throw new SettlementServiceUnavailableException(member, "unreadable response body", ex);
        }
    }

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300];

    // ── wire shapes (settlement-service Models/Contracts.cs) ──────────────────

    internal sealed class SettleWire
    {
        public required string DeliveryId { get; init; }
        public required string HolderId { get; init; }
        public required string ClientId { get; init; }
        public string? TierId { get; init; }
        public decimal? GrossAmount { get; init; }
        public required string Currency { get; init; }
        public required string PaymentMethod { get; init; }
        public DateTimeOffset? SettledAt { get; init; }
    }

    internal sealed class SettlementWire
    {
        public Guid SettlementId { get; init; }
        public string DeliveryId { get; init; } = string.Empty;
        public string HolderId { get; init; } = string.Empty;
        public string ClientId { get; init; } = string.Empty;
        public string TierId { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string Currency { get; init; } = SettlementService.CurrencyUsd;
        public string PaymentMethod { get; init; } = SettlementService.PaymentMethodCash;
        public decimal? GrossAmount { get; init; }
        public decimal? CommissionRate { get; init; }
        public decimal? CommissionAmount { get; init; }
        public decimal? NetAmount { get; init; }
        public DateTimeOffset? SettledAt { get; init; }
        public Guid? BatchId { get; init; }
        public DateTimeOffset? BatchedAt { get; init; }
        public DateTimeOffset? PaidAt { get; init; }
        public DateTimeOffset? ReceiptGeneratedAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    internal sealed class SettlementPageWire
    {
        public IReadOnlyList<SettlementWire>? Items { get; init; }
        public string? NextCursor { get; init; }
    }

    internal sealed class EarningsSummaryWire
    {
        public decimal GrossTotal { get; init; }
        public decimal CommissionTotal { get; init; }
        public decimal NetTotal { get; init; }
        public int Count { get; init; }
    }
}
