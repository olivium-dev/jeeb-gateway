using System;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// The detached delivery-status fan-out must be able to reach EVERY recipient.
///
/// <para><b>The defect this pins.</b> <see cref="DeliveryStatusPushNotifier"/> bounds each
/// recipient's send at <see cref="DeliveryStatusPushNotifier.PerRecipientTimeout"/> (8s) and
/// LINKS that timeout to the caller's token. The caller detached the fan-out under a flat
/// 10s ceiling. A first recipient that took its full 8s therefore left the second with 2s —
/// and both delivery-status call sites compose <c>[ClientId, JeeberId]</c> in that order, so
/// the recipient that loses its push is always the JEEBER. That is exactly the shared-deadline
/// starvation the per-recipient budget exists to prevent, re-imposed from outside the
/// notifier, and its only trace is a <c>TaskCanceledException</c> logged at warning.</para>
///
/// <para>Deliberately host-free: no WebApplicationFactory, no container, no upstream. The
/// invariant is arithmetic between two constants that live in different files, which is
/// precisely why it drifted.</para>
/// </summary>
public class DeliveryPushFanOutBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]  // the live shape: client + jeeber
    [InlineData(3)]
    public void Budget_Covers_Every_Recipients_Full_Per_Recipient_Timeout(int recipients)
    {
        var budget = DeliveriesController.DetachedPushBudgetFor(recipients);

        budget.Should().BeGreaterThan(
            DeliveryStatusPushNotifier.PerRecipientTimeout * recipients,
            "a ceiling shorter than recipients x the per-recipient budget silently truncates "
            + "the fan-out, and the recipient it drops is always the LAST one composed — the jeeber");
    }

    /// <summary>
    /// NEGATIVE CONTROL. The old flat 10s ceiling must FAIL the assertion above for the live
    /// two-recipient shape — otherwise the test is green for a reason unrelated to the fix
    /// and would have passed on the defect it claims to catch.
    /// </summary>
    [Fact]
    public void The_Old_Flat_Ceiling_Would_Have_Failed_This_Test()
    {
        var oldFlatCeiling = TimeSpan.FromSeconds(10);
        var neededForClientAndJeeber = DeliveryStatusPushNotifier.PerRecipientTimeout * 2;

        oldFlatCeiling.Should().BeLessThan(neededForClientAndJeeber,
            "10s against 2 x 8s is the starvation window; if this ever stops holding the "
            + "positive assertion above has lost its power");
    }

    /// <summary>
    /// A zero/blank recipient list still gets a non-degenerate ceiling — the notifier
    /// short-circuits before sending, but a zero-length CancellationTokenSource would cancel
    /// the scope resolution itself and log a spurious fan-out failure.
    /// </summary>
    [Fact]
    public void An_Empty_Recipient_List_Still_Gets_A_Usable_Ceiling()
    {
        DeliveriesController.DetachedPushBudgetFor(0)
            .Should().BeGreaterThan(TimeSpan.Zero);
    }
}
