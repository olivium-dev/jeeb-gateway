namespace JeebGateway.Financials;

/// <summary>
/// Pure receipt formatter (T-backend-016 / JEEB-34). Takes a persisted
/// <see cref="Settlement"/> and shapes the wire response — itemised
/// goods / commission / insurance lines, totals, and a deterministic
/// receipt number that mobile + admin can both quote in support.
/// </summary>
public static class ReceiptGenerator
{
    /// <summary>
    /// Builds a receipt for <paramref name="settlement"/>. The
    /// <paramref name="issuedAt"/> stamp is supplied by the caller so the
    /// generator stays time-pure for tests.
    /// </summary>
    public static ReceiptResponse Generate(Settlement settlement, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        var lines = new List<ReceiptLine>
        {
            new("Goods", settlement.GoodsCost),
            new($"Commission ({settlement.CommissionTier} {settlement.CommissionRate:P0})", settlement.Commission),
            new("Insurance", settlement.Insurance),
        };

        if (settlement.MinimumFeeApplied)
        {
            lines.Add(new ReceiptLine("Minimum-fee adjustment", 0m));
        }

        return new ReceiptResponse
        {
            ReceiptNumber = BuildReceiptNumber(settlement),
            DeliveryId = settlement.DeliveryId,
            SettlementId = settlement.Id,
            ClientId = settlement.ClientId,
            JeeberId = settlement.JeeberId,
            TierId = settlement.TierId,
            CommissionTier = settlement.CommissionTier.ToString(),
            Lines = lines,
            Total = settlement.Total,
            Currency = settlement.Currency,
            PaymentMethod = settlement.PaymentMethod,
            IssuedAt = issuedAt,
        };
    }

    /// <summary>
    /// Deterministic, customer-quotable receipt number. Format:
    /// <c>JB-{yyyyMMdd}-{first8 of settlement id}</c>. Date stamp uses
    /// the settled-at moment so re-reading the receipt months later
    /// still produces the same number.
    /// </summary>
    private static string BuildReceiptNumber(Settlement s)
    {
        var datePart = s.SettledAt.UtcDateTime.ToString("yyyyMMdd");
        var idPart = s.Id.Replace("-", string.Empty);
        if (idPart.Length > 8) idPart = idPart[..8];
        return $"JB-{datePart}-{idPart.ToUpperInvariant()}";
    }
}
