using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Financials;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// gwdbx W2-R11 stand-in for settlement-service, honouring the contract the gateway relies on:
/// one row per delivery id, a duplicate settle replays it, a settle carrying an amount PROMOTES an
/// amount-less intent. Set <see cref="Fault"/> to make every member throw the unavailable fault.
/// </summary>
public sealed class FakeSettlementServiceClient : ISettlementServiceClient
{
    public ConcurrentBag<SettlementSettleCommand> Settles { get; } = new();

    public ConcurrentDictionary<string, Settlement> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every call throws it — the "settlement-service is down" harness.</summary>
    public Func<string, Exception>? Fault { get; set; }

    public static FakeSettlementServiceClient Unreachable() => new()
    {
        Fault = member => new SettlementServiceUnavailableException(member, "connection refused"),
    };

    public Task<SettlementSettleResult> SettleAsync(SettlementSettleCommand command, CancellationToken ct)
    {
        Throw(nameof(SettleAsync));
        Settles.Add(command);

        if (Rows.TryGetValue(command.DeliveryId, out var existing))
        {
            // Promote an amount-less intent when a real amount finally arrives; otherwise replay.
            if (command.GrossAmount is > 0m && existing.State == SettlementState.PendingSettlement)
            {
                var promoted = Build(command, existing.Id);
                Rows[command.DeliveryId] = promoted;
                return Task.FromResult(new SettlementSettleResult(promoted, Created: true));
            }

            return Task.FromResult(new SettlementSettleResult(existing, Created: false));
        }

        var row = Build(command, Guid.NewGuid().ToString());
        Rows[command.DeliveryId] = row;
        return Task.FromResult(new SettlementSettleResult(row, Created: true));
    }

    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        Throw(nameof(GetByDeliveryAsync));
        return Task.FromResult(Rows.TryGetValue(deliveryId, out var row) ? row : null);
    }

    public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
    {
        Throw(nameof(GetByIdAsync));
        return Task.FromResult(Rows.Values.FirstOrDefault(r => r.Id == settlementId));
    }

    public Task<IReadOnlyList<Settlement>> ListAsync(SettlementListQuery query, CancellationToken ct)
    {
        Throw(nameof(ListAsync));
        return Task.FromResult<IReadOnlyList<Settlement>>(Rows.Values
            .Where(r => query.HolderId is null || r.JeeberId == query.HolderId)
            .Where(r => query.From is null || r.SettledAt >= query.From)
            .Where(r => query.To is null || r.SettledAt <= query.To)
            .Where(r => query.States is null || query.States.Count == 0 || query.States.Contains(r.CodState))
            .OrderBy(r => r.SettledAt)
            .ToArray());
    }

    public Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, CancellationToken ct)
    {
        Throw(nameof(MarkReceiptGeneratedAsync));
        var row = Rows.Values.FirstOrDefault(r => r.Id == settlementId);
        if (row is not null && row.ReceiptGeneratedAt is null)
        {
            row.ReceiptGeneratedAt = DateTimeOffset.UtcNow;
            row.State = SettlementState.ReceiptGenerated;
        }
        return Task.FromResult(row);
    }

    public Task<decimal> SumNetEarningsAsync(
        string? holderId, IReadOnlyCollection<string>? states,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        Throw(nameof(SumNetEarningsAsync));
        return Task.FromResult(Rows.Values
            .Where(r => holderId is null || r.JeeberId == holderId)
            .Where(r => states is null || states.Count == 0 || states.Contains(r.CodState))
            .Sum(r => r.GoodsCost - r.Commission));
    }

    private void Throw(string member)
    {
        if (Fault is not null) throw Fault(member);
    }

    private static Settlement Build(SettlementSettleCommand c, string id)
    {
        var breakdown = CommissionCalculator.Calculate(
            c.GrossAmount ?? 0m, CommissionCalculator.ResolveTier(c.TierId));
        return new Settlement
        {
            Id = id,
            DeliveryId = c.DeliveryId,
            ClientId = c.ClientId,
            JeeberId = c.HolderId,
            TierId = c.TierId ?? string.Empty,
            GoodsCost = breakdown.GoodsCost,
            CommissionTier = breakdown.Tier,
            CommissionRate = breakdown.CommissionRate,
            Commission = breakdown.Commission,
            Insurance = breakdown.Insurance,
            Total = breakdown.Total,
            MinimumFeeApplied = breakdown.MinimumFeeApplied,
            Currency = SettlementService.CurrencyUsd,
            PaymentMethod = c.PaymentMethod,
            State = c.GrossAmount is null or 0m
                ? SettlementState.PendingSettlement
                : SettlementState.Settled,
            SettledAt = c.SettledAt ?? DateTimeOffset.UtcNow,
        };
    }
}
