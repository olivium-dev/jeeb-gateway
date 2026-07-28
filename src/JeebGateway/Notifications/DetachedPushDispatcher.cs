using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// Runs a best-effort push fan-out BEHIND the response instead of in front of it.
/// </summary>
public interface IDetachedPushDispatcher
{
    /// <summary>
    /// Hands <paramref name="work"/> to a background task with a fresh DI scope and a ceiling
    /// sized by <see cref="PushSendBudget.ForFanOut"/>, and returns immediately. Never throws.
    /// </summary>
    /// <param name="label">Short seat name for the log line (e.g. <c>offer.accepted</c>).</param>
    /// <param name="recipientCount">How many recipients the work will send to; sizes the ceiling.</param>
    /// <param name="correlationId">Request/offer/delivery id, for the failure log only.</param>
    /// <param name="work">The fan-out, resolved against the fresh scope's provider.</param>
    void Dispatch(
        string label,
        int recipientCount,
        string correlationId,
        Func<IServiceProvider, CancellationToken, Task> work);
}

/// <inheritdoc />
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="PushSendBudget.PerRecipient"/> is 10s because that
/// is what a push to a recipient who actually owns a device costs. A budget that size cannot
/// sit in front of a user-visible response: the offer-accept seats awaited their fan-out
/// inline, so raising the cap there would have turned "the jeeber's push is silently
/// cancelled" into "the customer's accept blocks for 10s x (winner + every losing bidder)" —
/// past the mobile client's receive timeout, on a state change that has ALREADY COMMITTED.
/// That is JEBV4-281 exactly: the user is shown "No internet connection" about something that
/// succeeded. Fixing the cap without moving the await would have traded a push bug for a
/// worse UX bug.</para>
///
/// <para><b>Why a fresh DI scope, not just <c>Task.Run</c>.</b> The push notifiers are SCOPED
/// (they compose the scoped push client), and the request scope is disposed the instant the
/// response completes — a captured instance would outlive its scope and fault on a disposed
/// <c>HttpClient</c> handler chain. Same reason, same shape as
/// <c>DeliveriesController.DispatchDeliveryPushDetached</c> and
/// <c>NewRequestFanoutProcessor</c>; this type is those two patterns factored out so the next
/// seat cannot get it subtly wrong.</para>
///
/// <para><b>Why the caller's CancellationToken is deliberately NOT linked.</b> That token is
/// the request's. It is signalled when the client disconnects, and the whole point of
/// detaching is that the push outlives the response. Linking it would reintroduce the
/// cancellation this type exists to escape.</para>
/// </remarks>
public sealed class DetachedPushDispatcher : IDetachedPushDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DetachedPushDispatcher> _logger;

    public DetachedPushDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<DetachedPushDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispatch(
        string label,
        int recipientCount,
        string correlationId,
        Func<IServiceProvider, CancellationToken, Task> work)
    {
        var budget = PushSendBudget.ForFanOut(recipientCount);

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(budget);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await work(scope.ServiceProvider, cts.Token);
            }
            catch (Exception ex)
            {
                // Best-effort by contract: the response has already been written and the
                // state change already committed. Log it — a detached failure with no log is
                // exactly the silence that made this class of defect survive three rounds.
                _logger.LogWarning(ex,
                    "Detached push fan-out {Label} for {CorrelationId} failed "
                    + "(recipients={RecipientCount} budget={BudgetSeconds}s); the committed "
                    + "response stands.",
                    label, correlationId, recipientCount, (int)budget.TotalSeconds);
            }
        });
    }
}
