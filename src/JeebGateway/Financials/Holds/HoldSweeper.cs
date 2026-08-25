using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Availability;
using JeebGateway.Financials.Refunds;
using JeebGateway.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials.Holds;

/// <summary>DECISION Op 5 — the hold reconciler: per offer it joins intent record, wallet hold set
/// and offer status, then repairs ORPHAN, MISSING and STALE (wallet-service never expires, R4).</summary>
/// <remarks>Strict (an unreadable read SKIPS), wall-clock-free, never executes (I3), and with
/// <c>Holds:Enabled=false</c> drains leaks without placing anything.</remarks>
public class HoldSweeper : BackgroundService
{
    /// <summary>CONTRACT §4 `reason` for a hold released by this sweeper's forced withdraw.</summary>
    internal const string SweeperForcedReason = "sweeper-forced";

    private readonly IServiceProvider _services;
    private readonly JeeberSubmitSerializer _serializer;
    private readonly TimeProvider _clock;
    private readonly IOptions<HoldOptions> _options;
    private readonly ILogger<HoldSweeper> _logger;

    public HoldSweeper(
        IServiceProvider services,
        JeeberSubmitSerializer serializer,
        TimeProvider clock,
        IOptions<HoldOptions> options,
        ILogger<HoldSweeper> logger)
    {
        _services = services;
        _serializer = serializer;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Value.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "event={event}", "hold.sweep.failed");
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One reconciliation pass. Public so tests drive it directly against a fake
    /// clock — <see cref="ExecuteAsync"/> is never invoked from a test.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var options = _options.Value;
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var intents = sp.GetRequiredService<IHoldIntentStore>();
        var holds = sp.GetRequiredService<IHoldManager>();
        var wallet = sp.GetRequiredService<IWalletCommissionDebitClient>();
        var offers = sp.GetRequiredService<IPendingOffersStore>();
        var pushes = sp.GetRequiredService<IOfferPushNotifier>();
        var guard = sp.GetRequiredService<IWalletSufficiencyGuard>();

        // Two independent ledgers: refunds run FIRST and unconditionally, so a hold prefix-scan
        // outage can never also stall owed credits (W5-F2).
        await SweepRefundsAsync(sp, ct);

        IReadOnlyList<HoldIntent> records;
        try
        {
            records = await intents.ListAllAsync(ct) ?? Array.Empty<HoldIntent>();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The intent records ARE the map. Without them the only alternative is guessing
            // at live money, so the HOLD pass is skipped and retried next interval.
            _logger.LogWarning(ex,
                "event={event} reason={reason}", "hold.sweep.skipped", "intent-enumeration-failed");
            return;
        }

        var open = records
            .Where(r => r is not null
                        && !IsClosed(r.State)
                        && !string.IsNullOrWhiteSpace(r.JeeberId)
                        && !string.IsNullOrWhiteSpace(r.OfferId))
            .ToList();

        foreach (var group in open.GroupBy(r => r.JeeberId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepJeeberAsync(
                    group.Key, group.ToList(), options,
                    intents, holds, wallet, offers, pushes, guard, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-jeeber resilience: one unreadable holder must not strand every other
                // jeeber's leaked holds until the next pass.
                _logger.LogWarning(ex,
                    "event={event} jeeberId={jeeberId} reason={reason}",
                    "hold.sweep.skipped", group.Key, "jeeber-pass-faulted");
            }
        }
    }

    /// <summary>W5 §4 — re-drives open refund intents on the same cadence and re-reports
    /// conflicts. The intent is written BEFORE any credit, so a lost completion lands here.</summary>
    /// <remarks>Optional by design: a provider without the refund services sweeps holds exactly
    /// as before, and the ledger pre-checks make every replay converge instead of double-paying.</remarks>
    private async Task SweepRefundsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var intentStore = sp.GetService<IRefundIntentStore>();
        var refunder = sp.GetService<IFeeRefunder>();
        if (intentStore is null || refunder is null)
        {
            _logger.LogDebug(
                "event={event} reason={reason}", "fee.refund.sweep.skipped", "unwired");
            return;
        }

        IReadOnlyList<RefundIntent> records;
        try
        {
            records = await intentStore.ListAllAsync(ct) ?? Array.Empty<RefundIntent>();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Refunds are a separate ledger from holds: an unreadable one is retried next
            // interval and must never stop the hold pass that runs after it.
            _logger.LogWarning(ex,
                "event={event} reason={reason}",
                "fee.refund.sweep.skipped", "intent-enumeration-failed");
            return;
        }

        foreach (var intent in records)
        {
            if (intent is null)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            if (intent.State == RefundIntentState.Conflict)
            {
                // Same key, different money: an operator has to reconcile it, so it is
                // reported every pass and NEVER blind-retried into a second credit.
                _logger.LogWarning(
                    "event={event} requestId={requestId} amount={amount}",
                    "fee.refund.conflict", intent.RequestId, intent.Amount);
                continue;
            }

            if (intent.State != RefundIntentState.Open)
            {
                continue;
            }

            try
            {
                await refunder.TryRetryAsync(intent, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per-record resilience: one unreadable refund must not strand every other
                // jeeber's owed money until the next pass.
                _logger.LogWarning(ex,
                    "event={event} requestId={requestId} reason={reason}",
                    "fee.refund.sweep.skipped", intent.RequestId, "record-faulted");
            }
        }
    }

    private async Task SweepJeeberAsync(
        string jeeberId,
        IReadOnlyList<HoldIntent> records,
        HoldOptions options,
        IHoldIntentStore intents,
        IHoldManager holds,
        IWalletCommissionDebitClient wallet,
        IPendingOffersStore offers,
        IOfferPushNotifier pushes,
        IWalletSufficiencyGuard guard,
        CancellationToken ct)
    {
        // I5: the SAME per-jeeber critical section submit and edit-raise take, held for the
        // whole group — a repair can never interleave with an admission for this jeeber.
        using var lease = await _serializer.AcquireAsync(jeeberId, ct);

        var read = await offers.TryListForJeeberAsync(jeeberId, ct);
        if (read.Degraded)
        {
            // OD-C1-3 strict: an unreadable ledger means the offer side is UNKNOWN, and both
            // ways of guessing it wrong move real money. Skip this jeeber's records this pass.
            _logger.LogWarning(
                "event={event} jeeberId={jeeberId} reason={reason}",
                "hold.sweep.skipped", jeeberId, "offer-enumeration-degraded");
            return;
        }

        var mine = read.Items;
        var now = _clock.GetUtcNow();

        // Offers this pass has already retracted: the snapshot above still shows them live.
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);

        foreach (var intent in records)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReconcileAsync(
                    intent, mine, withdrawn, jeeberId, now, options,
                    intents, holds, wallet, offers, pushes, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "event={event} jeeberId={jeeberId} offerId={offerId} reason={reason}",
                    "hold.sweep.skipped", jeeberId, intent.OfferId, "record-faulted");
            }
        }

        if (!options.Enabled)
        {
            // Holds OFF (Layer A): the during-offer guarantee is no longer a hold, so the
            // sweeper's second job — periodic aggregate revalidation — is what remains.
            await RevalidateAggregateAsync(
                jeeberId, mine, withdrawn, now, guard, holds, offers, pushes, ct);
        }
    }

    private async Task ReconcileAsync(
        HoldIntent intent,
        IReadOnlyList<PendingOffer> mine,
        HashSet<string> withdrawn,
        string jeeberId,
        DateTimeOffset now,
        HoldOptions options,
        IHoldIntentStore intents,
        IHoldManager holds,
        IWalletCommissionDebitClient wallet,
        IPendingOffersStore offers,
        IOfferPushNotifier pushes,
        CancellationToken ct)
    {
        IReadOnlyList<HoldHeader> headers;
        try
        {
            // HoldManager owns the frozen key shape; reading it from there means the placer and
            // the reconciler can never address different hold sets.
            headers = await wallet.ListByExternalReferenceAsync(
                          HoldManager.ExternalReferenceFor(intent.OfferId), ct)
                      ?? Array.Empty<HoldHeader>();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Strict again: an unreadable hold set cannot be told apart from "no hold", and
            // treating a blip as "no hold" would double-place against the same offer.
            _logger.LogWarning(ex,
                "event={event} jeeberId={jeeberId} offerId={offerId} reason={reason}",
                "hold.sweep.skipped", jeeberId, intent.OfferId, "hold-set-unreadable");
            return;
        }

        // ONE predicate with the placer (HoldHeader.IsPending): two readers of the same
        // bytes disagreeing is a double-place on one side and a skipped repair on the other.
        var pending = headers.Where(h => h.IsPending).ToList();
        var offer = mine.FirstOrDefault(
            o => o is not null && string.Equals(o.Id, intent.OfferId, StringComparison.Ordinal));
        var live = offer is not null
                   && !withdrawn.Contains(intent.OfferId)
                   && PendingOfferStatus.IsLive(offer.Status);

        if (!live)
        {
            if (pending.Count == 0)
            {
                // STALE — nothing is held and the offer is over. A failed placement, or a
                // release whose tombstone write lost the race, both settle here.
                await CloseQuietlyAsync(intents, intent.OfferId, "stale-record", ct);
                return;
            }

            var grace = TimeSpan.FromMinutes(Math.Max(0, options.OrphanGraceMinutes));
            if (now - intent.PlacedAtUtc < grace)
            {
                // Inside the grace window the terminal transition's own release may still be
                // in flight; releasing now would race a legitimate submit-then-accept.
                return;
            }

            await ReleaseOrphanAsync(intent, pending, jeeberId, intents, wallet, ct);
            return;
        }

        // Rollback switch: with holds OFF the sweeper drains leaks but never places anything
        // new, so flipping the flag off really does stop hold creation everywhere.
        if (!options.Enabled)
        {
            return;
        }

        var expected = WalletGuardContract.RequiredCommission(offer!.Fee);
        var held = pending.Sum(h => h.Amount);
        if (held >= expected)
        {
            return;
        }

        // MISSING — a live offer that is not (fully) collateralised.
        await BackfillAsync(
            intent, offer, expected - held, mine, withdrawn, jeeberId, now,
            holds, offers, pushes, ct);
    }

    private async Task ReleaseOrphanAsync(
        HoldIntent intent,
        IReadOnlyList<HoldHeader> pending,
        string jeeberId,
        IHoldIntentStore intents,
        IWalletCommissionDebitClient wallet,
        CancellationToken ct)
    {
        var releasedAll = true;
        foreach (var header in pending)
        {
            try
            {
                // Abort is idempotent and can never clobber an executed header, so repeating
                // it across passes is safe; only a fresh failure keeps the record alive.
                await wallet.AbortAsync(header.TxId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                releasedAll = false;
                _logger.LogWarning(ex,
                    "event={event} jeeberId={jeeberId} offerId={offerId} txId={txId}",
                    "hold.leak.release_failed", jeeberId, intent.OfferId, header.TxId);
            }
        }

        // WARN, not Info: a leak reaching the sweeper means some terminal transition failed
        // to release, and that call site is the real defect this line points at.
        _logger.LogWarning(
            "event={event} jeeberId={jeeberId} offerId={offerId} requestId={requestId} "
            + "headers={headers} amount={amount} placedAtUtc={placedAtUtc} released={released}",
            "hold.leak.released", jeeberId, intent.OfferId, intent.RequestId,
            pending.Count, pending.Sum(h => h.Amount), intent.PlacedAtUtc, releasedAll);

        if (releasedAll)
        {
            await CloseQuietlyAsync(intents, intent.OfferId, "orphan-released", ct);
        }
    }

    private async Task BackfillAsync(
        HoldIntent intent,
        PendingOffer offer,
        decimal shortfall,
        IReadOnlyList<PendingOffer> mine,
        HashSet<string> withdrawn,
        string jeeberId,
        DateTimeOffset now,
        IHoldManager holds,
        IPendingOffersStore offers,
        IOfferPushNotifier pushes,
        CancellationToken ct)
    {
        if (!Guid.TryParse(jeeberId, out var jeeberGuid))
        {
            // E3-class: a non-holder id was never holdable, so there is no hold to repair.
            _logger.LogWarning(
                "event={event} jeeberId={jeeberId} offerId={offerId} reason={reason}",
                "hold.backfill.skipped", jeeberId, intent.OfferId, "jeeber-not-a-wallet-holder");
            return;
        }

        // Place only the SHORTFALL under the same external reference — the existing pending
        // headers already cover the rest, and a full re-place would silently over-hold.
        var placement = await holds.PlaceOnSubmitAsync(
            jeeberGuid, jeeberId, offer.Id, offer.RequestId, shortfall, ct);
        if (!placement.Insufficient)
        {
            _logger.LogInformation(
                "event={event} jeeberId={jeeberId} offerId={offerId} amount={amount} placed={placed}",
                "hold.backfill", jeeberId, offer.Id, shortfall, placement.Placed);
            return;
        }

        // Newest-first: the freshest bid is the least-committed exposure, so retiring it is
        // the smallest broken promise that can make the rest holdable again.
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in LiveOffers(mine, withdrawn, attempted).ToList())
        {
            ct.ThrowIfCancellationRequested();
            attempted.Add(candidate.Id);

            if (!await ForceWithdrawAsync(candidate, jeeberId, now, holds, offers, pushes, withdrawn, ct))
            {
                continue;
            }

            if (string.Equals(candidate.Id, offer.Id, StringComparison.Ordinal))
            {
                // The under-held offer was itself the newest exposure: it is gone now, so
                // there is nothing left to collateralise.
                return;
            }

            placement = await holds.PlaceOnSubmitAsync(
                jeeberGuid, jeeberId, offer.Id, offer.RequestId, shortfall, ct);
            if (!placement.Insufficient)
            {
                return;
            }
        }

        // Every live bid was retracted and the wallet still cannot cover this one. Visible,
        // because a live offer with no hold is exactly the state the epic exists to prevent.
        _logger.LogWarning(
            "event={event} jeeberId={jeeberId} offerId={offerId} shortfall={shortfall}",
            "hold.backfill.exhausted", jeeberId, offer.Id, shortfall);
    }

    /// <summary>Holds OFF: recompute the aggregate live commission and retract newest-first until it
    /// fits — same forced-withdraw + push + release as MISSING, so the modes look identical.</summary>
    private async Task RevalidateAggregateAsync(
        string jeeberId,
        IReadOnlyList<PendingOffer> mine,
        HashSet<string> withdrawn,
        DateTimeOffset now,
        IWalletSufficiencyGuard guard,
        IHoldManager holds,
        IPendingOffersStore offers,
        IOfferPushNotifier pushes,
        CancellationToken ct)
    {
        if (!Guid.TryParse(jeeberId, out var jeeberGuid))
        {
            return;
        }

        var attempted = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var outstanding = JeeberExposureCalculator.SumLiveCommission(LiveLegs(mine, withdrawn));
            if (outstanding <= 0m)
            {
                return;
            }

            var check = await guard.CheckAsync(jeeberGuid, outstanding, ct);
            if (check.DegradedByUpstreamFailure)
            {
                // A wallet blip is not a shortfall. Withdrawing good offers on an unreadable
                // balance would be a self-inflicted outage.
                _logger.LogWarning(
                    "event={event} jeeberId={jeeberId} reason={reason}",
                    "hold.revalidate.skipped", jeeberId, "wallet-degraded");
                return;
            }

            if (check.Allowed)
            {
                return;
            }

            var candidate = LiveOffers(mine, withdrawn, attempted).FirstOrDefault();
            if (candidate is null)
            {
                _logger.LogWarning(
                    "event={event} jeeberId={jeeberId} outstanding={outstanding} available={available}",
                    "hold.revalidate.exhausted", jeeberId, outstanding, check.Available);
                return;
            }

            // Marked attempted whether or not the retract lands, so a stubborn offer moves
            // the loop on to the next candidate instead of spinning on it.
            attempted.Add(candidate.Id);
            await ForceWithdrawAsync(candidate, jeeberId, now, holds, offers, pushes, withdrawn, ct);
        }
    }

    private async Task<bool> ForceWithdrawAsync(
        PendingOffer candidate,
        string jeeberId,
        DateTimeOffset now,
        IHoldManager holds,
        IPendingOffersStore offers,
        IOfferPushNotifier pushes,
        HashSet<string> withdrawn,
        CancellationToken ct)
    {
        WithdrawOfferOutcome outcome;
        try
        {
            outcome = await offers.TryWithdrawAsync(
                candidate.Id, candidate.RequestId, jeeberId, now, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "event={event} jeeberId={jeeberId} offerId={offerId} reason={reason}",
                "hold.forced_withdraw.failed", jeeberId, candidate.Id, "store-faulted");
            return false;
        }

        if (outcome != WithdrawOfferOutcome.Withdrawn)
        {
            _logger.LogInformation(
                "event={event} jeeberId={jeeberId} offerId={offerId} outcome={outcome}",
                "hold.forced_withdraw.skipped", jeeberId, candidate.Id, outcome);
            return false;
        }

        withdrawn.Add(candidate.Id);
        _logger.LogWarning(
            "event={event} reason={reason} jeeberId={jeeberId} offerId={offerId} requestId={requestId}",
            "hold.release", SweeperForcedReason, jeeberId, candidate.Id, candidate.RequestId);

        // CONTRACT §3 emitter 2. Without this the bid just disappears: the jeeber is told
        // what happened and sent to the one screen that fixes it.
        await pushes.NotifyOfferWithdrawnInsufficientBalanceAsync(
            jeeberId, candidate.RequestId, candidate.Id, ct);

        // Release LAST: a forced withdraw with a surviving hold is a leak, and this sweeper
        // is what would have to clean it up on a later pass anyway.
        await holds.ReleaseForOfferAsync(candidate.Id, SweeperForcedReason, ct);
        return true;
    }

    private async Task CloseQuietlyAsync(
        IHoldIntentStore intents, string offerId, string reason, CancellationToken ct)
    {
        try
        {
            await intents.CloseAsync(offerId, ct);
            _logger.LogInformation(
                "event={event} offerId={offerId} reason={reason}", "hold.record.closed", offerId, reason);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A tombstone that did not land costs one more pass over one record — never a
            // failed sweep, and never a released hold left unaccounted.
            _logger.LogWarning(ex,
                "event={event} offerId={offerId} reason={reason}",
                "hold.record.close_failed", offerId, reason);
        }
    }

    private static IEnumerable<PendingOffer> LiveOffers(
        IReadOnlyList<PendingOffer> mine, HashSet<string> withdrawn, HashSet<string> attempted)
        => mine
            .Where(o => o is not null
                        && !withdrawn.Contains(o.Id)
                        && !attempted.Contains(o.Id)
                        && PendingOfferStatus.IsLive(o.Status))
            .OrderByDescending(o => o.CreatedAt);

    private static IEnumerable<ExposureLeg> LiveLegs(
        IReadOnlyList<PendingOffer> mine, HashSet<string> withdrawn)
        => mine
            .Where(o => o is not null && !withdrawn.Contains(o.Id))
            .Select(o => new ExposureLeg(o.Id, o.RequestId, o.Fee, o.Status));

    /// <summary>Belt-and-braces over <c>ListAllAsync</c>, which already drops tombstones: the KV
    /// has no DELETE, so "closed" plus a short TTL is how a record stops existing.</summary>
    private static bool IsClosed(string? state)
        => string.Equals(state?.Trim(), HoldIntentState.Closed, StringComparison.OrdinalIgnoreCase);
}
