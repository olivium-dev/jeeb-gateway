using JeebGateway.Conversations.Client;
using JeebGateway.Requests;
using JeebGateway.Services;
using Microsoft.Extensions.Options;

namespace JeebGateway.Conversations;

/// <summary>
/// GW5 / W1.6-gateway — the heal pass for the post-accept chat settlement.
///
/// <para><b>Why a one-call settle is not enough on its own.</b> Folding seat + phase +
/// loser-removal into a single request removes the window BETWEEN two writes. It does
/// not remove the window between the caller's own commit and that request: the accept
/// saga commits upstream, the gateway then tries to settle, and anything from a
/// chat-service blip to the gateway process being killed mid-block loses the attempt.
/// The pre-GW5 code logged <i>"jeeber {JeeberId} may read 403 on chat until
/// reconciled"</i> — and nothing reconciled. This is that missing reconciler.</para>
///
/// <para><b>Where the work list comes from, and why not an outbox.</b> Candidates are
/// re-derived from the gateway's own DURABLE request rows: any request assigned a jeeber
/// inside the look-back window. That row is written by the accept projection
/// (<c>SetStatusAsync</c> / <c>SetJeeberIdAsync</c>) BEFORE the chat step runs, so a
/// kill anywhere in the chat step still leaves the evidence behind. An outbox row
/// written at the top of the chat step would itself be lost by a kill landing one
/// instruction earlier — it would defend against strictly less than this does, at the
/// cost of a table.</para>
///
/// <para><b>Divergence is decided by the AUTHORITY, not by a local flag.</b> Each
/// candidate is compared against chat-service's own view of the conversation: is the
/// phase settled, is the winner an ACTIVE participant, and is the active roster exactly
/// {owner, winner}? A gateway-local "settled" marker would be one more write that can be
/// lost, and a lost marker is indistinguishable from a lost settle. Asking the authority
/// costs one cheap read per candidate and cannot be wrong about its own roster.</para>
///
/// <para><b>Self-terminating.</b> A settled conversation is not divergent, so a healed
/// request costs one read per sweep and then ages out of the window. Every candidate is
/// isolated in its own try/catch: one still-broken row (chat-service still down) can
/// never wedge the sweep.</para>
///
/// <para><b>Convergent, so replay-safe.</b> The settle states an end state, and
/// chat-service reconciles its direct-read projection on EVERY settle including a replay
/// (<c>already_settled: true</c>). That is why this class never branches on that flag to
/// skip work — doing so would disable the projection repair the replay exists for.</para>
/// </summary>
public sealed class AcceptChatSettleReconciler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<AcceptChatSettleReconcilerOptions> _options;
    private readonly UpstreamFeatureFlags _flags;
    private readonly ILogger<AcceptChatSettleReconciler> _logger;

    public AcceptChatSettleReconciler(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<AcceptChatSettleReconcilerOptions> options,
        IOptions<UpstreamFeatureFlags> flags,
        ILogger<AcceptChatSettleReconciler> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _flags = flags.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled || !_flags.Chat)
        {
            _logger.LogInformation(
                "Accept-settle reconciler disabled (enabled={Enabled}, chatFlag={ChatFlag}); "
                + "post-accept chat settlements will NOT be healed if the inline attempt is lost.",
                _options.Value.Enabled, _flags.Chat);
            return;
        }

        var interval = _options.Value.SweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            // DELAY FIRST, sweep second. Two reasons, and the second is the one that
            // bites:
            //   1. A sweep racing host startup competes with warm-up for the same
            //      chat-service connections, for candidates that the inline settle is
            //      about to handle anyway.
            //   2. A boot-time sweep makes this service's behaviour NON-DETERMINISTIC
            //      inside any WebApplicationFactory host that enables the Chat flag: the
            //      sweep and the test's own accept race, and a test asserting "exactly
            //      one settle" passes or fails on thread-pool timing. A green suite that
            //      depends on losing a race is not evidence of anything.
            // The cost is that a restart's heal is deferred by one interval, which is
            // bounded, stated, and far cheaper than a flaky money-path assertion.
            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

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
                _logger.LogError(ex, "Accept-settle reconcile sweep failed");
            }
        }
    }

    /// <summary>
    /// One reconcile pass. Returns the number of requests actually RE-SETTLED (not the
    /// number inspected) so a test can assert healing rather than merely activity.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var opts = _options.Value;

        using var scope = _services.CreateScope();
        var requests = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var conversations = scope.ServiceProvider.GetRequiredService<IJeebConversationClient>();
        var settler = scope.ServiceProvider.GetRequiredService<IAcceptChatSettler>();

        var since = _clock.GetUtcNow() - opts.LookBack;
        var candidates = await requests.ListAssignedSinceAsync(since, opts.PageSize, ct);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var reconciled = 0;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var jeeberId = row.JeeberId;
            if (string.IsNullOrWhiteSpace(jeeberId))
            {
                continue;
            }

            try
            {
                if (!await IsDivergentAsync(conversations, row, jeeberId, ct))
                {
                    continue;
                }

                ChatSettleTelemetry.ReconcileDivergent.Add(1);

                var result = await settler.SettleAsync(row, jeeberId, ct);
                if (result.Status == AcceptChatSettleStatus.Settled)
                {
                    ChatSettleTelemetry.Reconciled.Add(1);
                    reconciled++;
                    _logger.LogWarning(
                        "Accept-settle reconcile HEALED request {RequestId} (conversation "
                        + "{ConversationId}, jeeber {JeeberId}): the inline post-accept settle "
                        + "had not landed. alreadySettled={AlreadySettled}.",
                        row.Id, result.ConversationId, jeeberId, result.AlreadySettled);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unhealable row must never wedge the sweep. Counted, not swallowed
                // silently — chat.accept_settle.failures is the signal that a winner is
                // still locked out.
                ChatSettleTelemetry.Failures.Add(1);
                _logger.LogWarning(ex,
                    "Accept-settle reconcile for request {RequestId} (jeeber {JeeberId}) failed; "
                    + "the winner may still read 403 on chat. Retrying next sweep.",
                    row.Id, jeeberId);
            }
        }

        return reconciled;
    }

    /// <summary>
    /// Ask chat-service — the membership authority — whether this request's conversation
    /// is in the settled 1:1 end state the accept path asked for.
    ///
    /// <para>Divergent when ANY of: the conversation does not exist; the phase is not
    /// <see cref="AcceptChatSettler.SettledPhase"/>; the winner is absent or soft-removed;
    /// or an active participant exists who is neither the owner nor the winner (a losing
    /// bidder left seated — simultaneously a leak risk and the reason a winner reads a
    /// blank thread).</para>
    ///
    /// <para>A read fault is NOT treated as divergence: a chat-service blip would
    /// otherwise stampede a settle for every candidate at once. It propagates to the
    /// per-row catch and is retried next sweep.</para>
    /// </summary>
    private static async Task<bool> IsDivergentAsync(
        IJeebConversationClient conversations,
        DeliveryRequest row,
        string winningJeeberId,
        CancellationToken ct)
    {
        JeebConversationResponse? convo;
        try
        {
            convo = await conversations.GetConversationByCorrelationAsync(row.Id, ct);
        }
        catch (JeebConversationApiException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // No conversation at all for an ACCEPTED request: the inline ensure never
            // ran or never landed. The settler creates it.
            return true;
        }

        if (convo is null || string.IsNullOrWhiteSpace(convo.ConversationId))
        {
            return true;
        }

        if (!string.Equals(convo.Phase, AcceptChatSettler.SettledPhase, StringComparison.Ordinal))
        {
            return true;
        }

        // A null roster is not "no strays" — it is an answer we cannot read. Treat it as
        // divergent (a settle is convergent, so a needless one is harmless) rather than
        // letting it NRE into the per-row catch and be retried forever as a fault.
        if (convo.Participants is null)
        {
            return true;
        }

        var active = convo.Participants
            .Where(p => p.RemovedAt is null && !string.IsNullOrWhiteSpace(p.UserId))
            .ToList();

        var winnerActive = active.Any(p =>
            string.Equals(p.UserId, winningJeeberId, StringComparison.Ordinal)
            && string.Equals(p.RoleInConvo, AcceptChatSettler.WinnerRole, StringComparison.Ordinal));
        if (!winnerActive)
        {
            return true;
        }

        // The settled roster is exactly {owner, winner}. Anyone else still active is a
        // losing bidder the phase advance failed to remove.
        var strayActive = active.Any(p =>
            !string.Equals(p.UserId, winningJeeberId, StringComparison.Ordinal)
            && !string.Equals(p.UserId, row.ClientId, StringComparison.Ordinal));

        return strayActive;
    }
}
