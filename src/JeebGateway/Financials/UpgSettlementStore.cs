using System.Globalization;
using System.Text.Json;
using JeebGateway.Financials.Cod;

namespace JeebGateway.Financials;

/// <summary>
/// Compatibility projection over UPG's authoritative COD records. This class
/// implements the older gateway read seam without owning rows, batch state,
/// idempotency memos, or scheduled processing.
/// </summary>
public sealed class UpgSettlementStore : ISettlementStore
{
    private readonly ICodSettlementLedger _owner;
    private readonly IHttpClientFactory _clients;

    public UpgSettlementStore(
        ICodSettlementLedger owner,
        IHttpClientFactory clients)
    {
        _owner = owner;
        _clients = clients;
    }

    public async Task<(Settlement Row, bool Inserted)> TryInsertAsync(
        Settlement settlement, CancellationToken ct)
    {
        if (string.Equals(settlement.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
        {
            if (settlement.GoodsCost <= 0m)
                return (settlement, false);
            var intent = await _owner.UpsertCodIntentAsync(ToIntent(settlement), ct);
            EnsureOwnerSuccess(intent);
            return (MapCod(intent.Body!, settlement), intent.StatusCode == StatusCodes.Status201Created);
        }

        var result = await _owner.RecordCodAsync(ToRecord(settlement), ct);
        EnsureOwnerSuccess(result);
        return (MapCod(result.Body!, settlement), result.StatusCode == StatusCodes.Status201Created);
    }

    public async Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        var result = await _owner.GetCodByDeliveryAsync(deliveryId, ct);
        if (result.Available && result.StatusCode == StatusCodes.Status404NotFound) return null;
        EnsureOwnerSuccess(result);
        return MapCod(result.Body!, null);
    }

    public async Task<IReadOnlyList<Settlement>> ListByJeeberAsync(
        string jeeberId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct,
        IReadOnlyCollection<string>? codStates = null)
    {
        var baseQuery = new List<string>
        {
            "providerId=" + Uri.EscapeDataString(jeeberId),
            "sort=createdAt:asc",
            "limit=100",
        };
        if (from is not null) baseQuery.Add("from=" + Uri.EscapeDataString(from.Value.ToUniversalTime().ToString("o")));
        if (to is not null) baseQuery.Add("to=" + Uri.EscapeDataString(to.Value.ToUniversalTime().ToString("o")));

        var rows = new List<Settlement>();
        var states = codStates is { Count: > 0 }
            ? codStates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : new string?[] { null };
        foreach (var state in states)
        {
            var query = new List<string>(baseQuery);
            if (!string.IsNullOrWhiteSpace(state))
                query.Add("status=" + Uri.EscapeDataString(ToOwnerStatus(state)));
            await ReadAllAdminPagesAsync(query, rows, ct);
        }

        return rows
            .GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.SettledAt)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task ReadAllAdminPagesAsync(
        IReadOnlyList<string> baseQuery, List<Settlement> destination, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var query = new List<string>(baseQuery);
            if (!string.IsNullOrWhiteSpace(cursor))
                query.Add("cursor=" + Uri.EscapeDataString(cursor));
            using var document = await GetAdminAsync(
                "admin/v1/settlements?" + string.Join('&', query), ct);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray()) destination.Add(MapAdmin(item));

            cursor = document.RootElement.TryGetProperty("page", out var page)
                     && page.ValueKind == JsonValueKind.Object
                     && page.TryGetProperty("nextCursor", out var next)
                     && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));
    }

    public async Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
    {
        try
        {
            using var document = await GetAdminAsync(
                $"admin/v1/settlements/{Uri.EscapeDataString(settlementId)}", ct);
            return MapAdmin(document.RootElement.GetProperty("data"));
        }
        catch (OwnerNotFoundException)
        {
            return null;
        }
    }

    public Task<Settlement?> MarkReceiptGeneratedAsync(
        string settlementId, DateTimeOffset at, CancellationToken ct) => GetByIdAsync(settlementId, ct);

    public async Task<bool> ReplacePendingAsync(
        string deliveryId, Settlement settled, CancellationToken ct)
    {
        // The owner requires optimistic concurrency for the intent -> pending
        // transition. Read the durable owner row here instead of carrying a
        // gateway-side version cache; the gateway remains stateless.
        var existing = await _owner.GetCodByDeliveryAsync(deliveryId, ct);
        if (existing.Available && existing.StatusCode == StatusCodes.Status404NotFound)
            return false;
        EnsureOwnerSuccess(existing);

        var expectedVersion = OwnerVersion(existing.Body!);
        var finalized = await _owner.FinalizeCodIntentAsync(
            ToFinalize(settled, expectedVersion), ct);
        if (finalized.Available && finalized.StatusCode == StatusCodes.Status404NotFound)
            return false;
        EnsureOwnerSuccess(finalized);

        // Both the first transition and an exact owner-side idempotent replay
        // return 200. In either case the payable row exists, so the caller must
        // not fall through to the legacy record endpoint.
        return true;
    }

    private async Task<JsonDocument> GetAdminAsync(string path, CancellationToken ct)
    {
        var client = _clients.CreateClient("admin-settlements");
        if (client.BaseAddress is null)
            throw new HttpRequestException("COD owner is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Admin-Id", "jeeb-gateway");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new OwnerNotFoundException();
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static CodRecordRequest ToRecord(Settlement row) => new(
        row.DeliveryId,
        row.JeeberId,
        row.GoodsCost,
        row.CommissionRate,
        row.Commission,
        row.Currency,
        new Dictionary<string, string>
        {
            ["client_id"] = row.ClientId,
            ["tier_id"] = row.TierId,
            ["payment_method"] = row.PaymentMethod,
        });

    private static CodIntentRequest ToIntent(Settlement row) => new(
        row.DeliveryId,
        row.JeeberId,
        row.GoodsCost,
        row.CommissionRate,
        row.Currency,
        SnapshotSequence: 1,
        Metadata(row));

    private static CodFinalizeIntentRequest ToFinalize(Settlement row, int expectedVersion) => new(
        row.DeliveryId,
        row.JeeberId,
        row.GoodsCost,
        row.CommissionRate,
        row.Currency,
        expectedVersion,
        SnapshotSequence: 2,
        IdempotencyKey: $"cod-intent:{row.DeliveryId}:finalize:2",
        Metadata(row));

    private static IReadOnlyDictionary<string, string> Metadata(Settlement row) =>
        new Dictionary<string, string>
        {
            ["client_id"] = row.ClientId,
            ["tier_id"] = row.TierId,
            ["payment_method"] = row.PaymentMethod,
        };

    private static void EnsureOwnerSuccess(CodLedgerResult result)
    {
        if (!result.Available) throw new HttpRequestException("COD owner is unavailable.");
        if (result.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"COD owner returned HTTP {result.StatusCode}.");
        if (string.IsNullOrWhiteSpace(result.Body)) throw new InvalidDataException("COD owner returned no body.");
    }

    private static Settlement MapCod(string body, Settlement? template)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var nested) ? nested : root;
        var metadata = data.TryGetProperty("metadata", out var metadataNode) ? metadataNode : default;
        var gross = MoneyAny(data, "gross_amount", "grossAmount");
        var rate = MoneyAny(data, "commission_rate", "commissionRate");
        var commission = MoneyAny(data, "commission_amount", "commissionAmount");
        var ownerStatus = TextAny(data, "status");

        return new Settlement
        {
            Id = Text(data, "id") ?? template?.Id ?? string.Empty,
            DeliveryId = TextAny(data, "delivery_id", "deliveryId", "external_reference", "externalReference")
                         ?? template?.DeliveryId ?? string.Empty,
            ClientId = Metadata(metadata, "client_id") ?? template?.ClientId ?? string.Empty,
            JeeberId = TextAny(data, "provider_id", "providerId", "jeeber_id", "jeeberId")
                       ?? template?.JeeberId ?? string.Empty,
            TierId = Metadata(metadata, "tier_id") ?? template?.TierId ?? string.Empty,
            GoodsCost = gross,
            CommissionTier = CommissionCalculator.ResolveTier(Metadata(metadata, "tier_id") ?? template?.TierId),
            CommissionRate = rate,
            Commission = commission,
            Insurance = 0,
            Total = commission,
            MinimumFeeApplied = false,
            Currency = TextAny(data, "currency") ?? template?.Currency ?? SettlementService.CurrencyUsd,
            PaymentMethod = Metadata(metadata, "payment_method") ?? SettlementService.PaymentMethodCash,
            State = string.Equals(ownerStatus, "intent", StringComparison.OrdinalIgnoreCase)
                ? SettlementState.PendingSettlement
                : SettlementState.Settled,
            CodState = MapCodState(ownerStatus),
            SettledAt = Date(TextAny(data, "created_at", "createdAt")) ?? template?.SettledAt ?? DateTimeOffset.UnixEpoch,
            BatchedAt = Date(TextAny(data, "batched_at", "batchedAt")),
            PaidAt = Date(TextAny(data, "paid_at", "paidAt")),
            LedgerEntryId = TextAny(data, "id"),
        };
    }

    private static Settlement MapAdmin(JsonElement data)
    {
        var metadataTemplate = new Settlement
        {
            Id = Text(data, "id") ?? string.Empty,
            DeliveryId = Text(data, "deliveryId") ?? string.Empty,
            ClientId = string.Empty,
            JeeberId = Text(data, "providerId") ?? string.Empty,
            TierId = string.Empty,
            GoodsCost = Money(data, "grossAmount"),
            CommissionTier = CommissionTier.Standard,
            CommissionRate = CommissionCalculator.FlatRate,
            Commission = Money(data, "commissionAmount"),
            Insurance = 0,
            Total = Money(data, "commissionAmount"),
            MinimumFeeApplied = false,
            Currency = Text(data, "currency") ?? SettlementService.CurrencyUsd,
            PaymentMethod = SettlementService.PaymentMethodCash,
            State = SettlementState.Settled,
            CodState = MapCodState(Text(data, "status")),
            SettledAt = Date(Text(data, "createdAt")) ?? DateTimeOffset.UnixEpoch,
            BatchedAt = Date(Text(data, "batchedAt")),
            PaidAt = Date(Text(data, "paidAt")),
            LedgerEntryId = Text(data, "id"),
        };
        return metadataTemplate;
    }

    private static string MapCodState(string? status) => status switch
    {
        "batched" => CodSettlementState.Batched,
        "paid" => CodSettlementState.Paid,
        _ => CodSettlementState.Recorded,
    };

    private static string ToOwnerStatus(string gatewayStatus) =>
        gatewayStatus.Trim().ToLowerInvariant() switch
        {
            CodSettlementState.Recorded => "pending",
            CodSettlementState.Batched => "batched",
            CodSettlementState.Paid => "paid",
            _ => throw new ArgumentOutOfRangeException(nameof(gatewayStatus), gatewayStatus,
                "Unsupported gateway COD status filter."),
        };

    private static decimal Money(JsonElement data, string property) =>
        data.TryGetProperty(property, out var value)
        && decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private static decimal MoneyAny(JsonElement data, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out _))
                return Money(data, property);
        }
        return 0m;
    }

    private static string? Text(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static string? TextAny(JsonElement data, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = Text(data, property);
            if (value is not null) return value;
        }
        return null;
    }

    private static int OwnerVersion(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var nested) ? nested : root;
        if (data.TryGetProperty("version", out var version)
            && (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var numeric)
                || version.ValueKind == JsonValueKind.String
                && int.TryParse(version.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric)))
            return numeric;
        throw new InvalidDataException("COD owner response did not include a valid version.");
    }

    private static string? Metadata(JsonElement metadata, string property) =>
        metadata.ValueKind == JsonValueKind.Object ? Text(metadata, property) : null;

    private static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private sealed class OwnerNotFoundException : Exception
    {
    }
}
