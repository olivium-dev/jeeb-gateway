using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.JeebWallet;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Financials;

/// <summary>
/// OQ1 (F1, UNRESOLVED — blocks merge): fail-open or fail-closed when wallet-service
/// is unreachable. Config, not code — default fail-closed, pending owner ratification.
/// </summary>
public sealed class WalletGuardOptions
{
    public const string SectionName = "WalletGuard";

    /// <summary>"fail-open" or "fail-closed"; anything else resolves to fail-closed.</summary>
    public string FailMode { get; set; } = "fail-closed";

    public bool IsFailOpen => string.Equals(FailMode, "fail-open", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Needed/available/currency feed the 402/409 body (correction 5, top-level).</summary>
public sealed record WalletGuardResult(
    bool Allowed,
    decimal Required,
    decimal? Available,
    string? Currency,
    bool DegradedByUpstreamFailure);

public interface IWalletSufficiencyGuard
{
    /// <summary>Never throws — a wallet-service failure resolves via <see cref="WalletGuardOptions.FailMode"/>.</summary>
    Task<WalletGuardResult> CheckAsync(Guid holderId, decimal requiredFee, CancellationToken ct);
}

/// <summary>Shared pieces of the three guard call sites so they cannot drift apart.</summary>
public static class WalletGuardContract
{
    /// <summary>Same constant AND same rounding as CommissionCalculator (AwayFromZero).</summary>
    public static decimal RequiredCommission(decimal fee) =>
        Math.Round(fee * CommissionCalculator.FlatRate, 2, MidpointRounding.AwayFromZero);

    /// <summary>Fail-closed on a wallet-service outage is a distinct 503 — never a
    /// fabricated 402/409 "insufficient balance" the caller would act on.</summary>
    public static Microsoft.AspNetCore.Mvc.ProblemDetails WalletUnavailableProblem() => new()
    {
        Title = "Wallet service is unavailable; the balance check could not run.",
        Status = StatusCodes.Status503ServiceUnavailable,
        Type = "https://jeeb.dev/errors/wallet-service-unavailable",
    };
}

/// <summary>
/// F1 — shared balance-sufficiency primitive for the submit/accept/edit guards.
/// Reuses the already-DI-wired <see cref="ServiceWalletClient"/>, no new client.
/// </summary>
public sealed class WalletSufficiencyGuard : IWalletSufficiencyGuard
{
    private readonly ServiceWalletClient _wallet;
    private readonly WalletGuardOptions _options;
    private readonly int _feeCurrencyId;
    private readonly string _feeCurrencyCode;
    private readonly ILogger<WalletSufficiencyGuard> _logger;

    public WalletSufficiencyGuard(
        ServiceWalletClient wallet,
        IOptions<WalletGuardOptions> options,
        IOptions<CommissionCollectionOptions> feeCurrency,
        ILogger<WalletSufficiencyGuard> logger)
    {
        _wallet = wallet;
        _options = options.Value;
        // Same options object the commission debit uses, so checked and charged share one currency.
        _feeCurrencyId = feeCurrency.Value.CurrencyId;
        _feeCurrencyCode = feeCurrency.Value.CurrencyCode;
        _logger = logger;
    }

    public async Task<WalletGuardResult> CheckAsync(Guid holderId, decimal requiredFee, CancellationToken ct)
    {
        GetHolderWallets? holder;
        try
        {
            holder = await _wallet.WalletsAsync(holderId, ct);
        }
        catch (WalletApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            // No holder/wallet provisioned yet: an honest zero balance, not a failure.
            holder = null;
        }
        catch (Exception ex) when (ex is WalletApiException or HttpRequestException
            or BrokenCircuitException or TimeoutRejectedException)
        {
            // Correction 8: also catch the breaker-open/timeout wrapper types, or
            // fail-open silently fails closed the moment ServiceWalletClient's breaker trips.
            _logger.LogWarning(ex,
                "WalletSufficiencyGuard: wallet-service unreachable for holder {HolderId}; "
                + "FailMode={FailMode} (OQ1 pending owner ratification).", holderId, _options.FailMode);
            return new WalletGuardResult(_options.IsFailOpen, requiredFee, null, _feeCurrencyCode, DegradedByUpstreamFailure: true);
        }

        var (available, currency) = ProjectFeeCurrencyBalance(holder);
        return new WalletGuardResult(available >= requiredFee, requiredFee, available, currency, DegradedByUpstreamFailure: false);
    }

    /// <summary>
    /// Correction 6 (c3): sum ONLY the spendable balance in the fee currency — the currency the
    /// commission is actually debited in. No FX, no blend: a rate is NEVER applied here.
    /// </summary>
    private (decimal Available, string Currency) ProjectFeeCurrencyBalance(GetHolderWallets? holder)
    {
        // R-M1 (G-01): non-spendable cod_* legs are dropped too, so COD float never pays the fee.
        var available = (holder?.Wallets ?? new List<Wallet>())
            .Where(w => w.IsActive && SpendableWalletTypes.IsSpendable(w.Type) && w.CurrencyID == _feeCurrencyId)
            .Sum(w => (decimal)w.Amount);

        return (available, _feeCurrencyCode);
    }
}
