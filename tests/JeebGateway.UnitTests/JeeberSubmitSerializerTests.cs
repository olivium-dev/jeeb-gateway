using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.UnitTests;

// W3/c1 (G1, T1): the per-jeeber critical section that closes the submit TOCTOU. Sequencing is
// proven with TaskCompletionSource + pending-task state only — no Task.Delay, no wall clock.
public class JeeberSubmitSerializerTests
{
    private const string JeeberA = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";
    private const string JeeberB = "3c1d0f4e-2b7a-4c55-9d10-8f6e5a4b3c2d";

    [Fact]
    public async Task SameJeeber_SecondWaitsForFirstRelease()
    {
        var serializer = new JeeberSubmitSerializer();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await serializer.AcquireAsync(JeeberA, CancellationToken.None);
        var second = EnterAndSignalAsync(serializer, JeeberA, secondEntered);

        // While the first handle is held the second acquire cannot have completed.
        Assert.False(secondEntered.Task.IsCompleted);
        Assert.False(second.IsCompleted);

        first.Dispose();

        await secondEntered.Task;
        await second;
    }

    [Fact]
    public async Task DifferentJeebers_DoNotBlockEachOther()
    {
        var serializer = new JeeberSubmitSerializer();

        var handleA = await serializer.AcquireAsync(JeeberA, CancellationToken.None);
        // Would deadlock under a global lock; per-jeeber striping lets B through.
        var handleB = await serializer.AcquireAsync(JeeberB, CancellationToken.None);

        Assert.NotNull(handleB);

        // A is genuinely still held: a second A acquire is still pending.
        var contestedA = serializer.AcquireAsync(JeeberA, CancellationToken.None);
        Assert.False(contestedA.IsCompleted);

        handleB.Dispose();
        handleA.Dispose();

        (await contestedA).Dispose();
    }

    [Fact]
    public async Task SameJeeber_CancelledWaiter_ReleasesWait_AndLeavesLockAcquirable()
    {
        var serializer = new JeeberSubmitSerializer();
        using var cts = new CancellationTokenSource();

        var first = await serializer.AcquireAsync(JeeberA, CancellationToken.None);
        var waiting = serializer.AcquireAsync(JeeberA, cts.Token);
        Assert.False(waiting.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // The cancelled waiter must not have consumed the slot the first handle releases.
        first.Dispose();
        var next = await serializer.AcquireAsync(JeeberA, CancellationToken.None);
        Assert.NotNull(next);
        next.Dispose();
    }

    private static async Task EnterAndSignalAsync(
        JeeberSubmitSerializer serializer, string jeeberId, TaskCompletionSource entered)
    {
        using var handle = await serializer.AcquireAsync(jeeberId, CancellationToken.None);
        entered.SetResult();
    }
}
