using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>
/// TEST-ONLY <see cref="IDetachedPushDispatcher"/> that keeps the handle the real one throws away.
///
/// <para><b>The defect it closes.</b> The offer push seats dispatch BEHIND the response on purpose
/// (JEBV4-281: a <see cref="PushSendBudget.PerRecipient"/>-sized await cannot sit in front of a
/// user-visible response). A test that reads the push spy on the line after the response is
/// therefore asserting on work that is only <c>Task.Run</c>-queued, with nothing ordering the two.
/// It passes whenever the pool happens to pick the item up first and fails when it does not — the
/// A7 CI flake, where run 31675937650's own log shows the 201 finishing at 07:00:31.8852029 and the
/// push landing at 07:00:31.8870762, ~1.8ms after the assertion had already read an empty queue.</para>
///
/// <para>This runs the SAME delegate in the SAME fresh DI scope as
/// <see cref="DetachedPushDispatcher"/> and only records the resulting task, so a test can await
/// the seat instead of the thread pool. There is no wall clock, no polling and no retry: a
/// notifier that is never dispatched produces no task, so an over-deleted push seat still goes red.
/// STATED CEILING: substituting this proves nothing about <see cref="DetachedPushDispatcher"/>
/// itself — that the seats hold that seam is <c>PushSendBudgetRegressionTests</c>'s assertion.</para>
/// </summary>
public sealed class AwaitableDetachedPushDispatcher : IDetachedPushDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ConcurrentQueue<Task> _dispatched = new();

    public AwaitableDetachedPushDispatcher(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Registers this double over the gateway's own dispatcher.</summary>
    public static void Use(IServiceCollection services)
    {
        services.RemoveAll<IDetachedPushDispatcher>();
        services.AddSingleton<AwaitableDetachedPushDispatcher>();
        services.AddSingleton<IDetachedPushDispatcher>(
            sp => sp.GetRequiredService<AwaitableDetachedPushDispatcher>());
    }

    /// <summary>Completes when every fan-out dispatched so far has finished.</summary>
    public Task DispatchedWork => Task.WhenAll(_dispatched.ToArray());

    public void Dispatch(
        string label,
        int recipientCount,
        string correlationId,
        Func<IServiceProvider, CancellationToken, Task> work)
        => _dispatched.Enqueue(RunAsync(work));

    private async Task RunAsync(Func<IServiceProvider, CancellationToken, Task> work)
    {
        using var scope = _scopes.CreateScope();
        await work(scope.ServiceProvider, CancellationToken.None);
    }
}
