using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using JeebGateway.service.ServicePushNotification;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Every push seat's per-recipient cap must be able to survive a HEALTHY push.
///
/// <para><b>The defect this pins.</b> Six push seats each owned a private
/// <c>PushTimeout</c>/<c>PerSendTimeout</c> copy, five of them 2s, sized from the claim that
/// "the LAN-local push svc is normally &lt;200ms". Measured against the live push service on
/// 2026-07-28, POSITIVE CONTROL FIRST: a recipient with NO device rows answers 404 in
/// 13.9/15.7/14.0 ms — that is the &lt;200ms the caps were sized from, and it describes a push
/// with nothing to deliver. A recipient who actually owns a device costs 2.532 / 2.567 /
/// 2.571 / 2.667 / 3.019 / 3.351 / 3.373 / 3.568 / 3.674 / 3.969 s, because the endpoint walks
/// every registered device row sequentially at ~170ms of FCM round trip each and the two live
/// accounts carry 19 and 24 rows. <b>Ten out of ten healthy calls exceeded 2s.</b> So the cap
/// did not bound a slow push service — it guaranteed that a push to a recipient who has a
/// phone was the only kind that could never complete.</para>
///
/// <para><b>Why this test is reflective rather than a list of constants.</b> JEBV4-345 found
/// this exact defect, fixed the copy in <see cref="ChatMessagePushNotifier"/>, and left five
/// siblings at 2s. A test naming today's seats would go green on tomorrow's seventh seat with
/// a fresh 2s copy. This walks the compiled gateway assembly for every static per-send cap and
/// holds all of them to the floor — with
/// <see cref="Discovery_Finds_Every_Known_Push_Seat"/> as its positive control, because a
/// reflective scan that quietly finds nothing is indistinguishable from one that passes.</para>
///
/// <para>Deliberately host-free: no WebApplicationFactory, no container, no upstream.</para>
/// </summary>
public class PushSendBudgetRegressionTests
{
    /// <summary>
    /// The slowest of the ten healthy calls measured on 2026-07-28. A cap at or below this is
    /// a cap that the healthy path cannot clear — which is the whole defect.
    /// </summary>
    private static readonly TimeSpan MeasuredWorstHealthyCall = TimeSpan.FromMilliseconds(3969);

    /// <summary>
    /// The push HttpClient's own resilience-pipeline timeout
    /// (<c>ServiceClientExtensions.ConfigurePushBreakerAndTimeout</c>). A per-send cap above
    /// this is dead code the pipeline pre-empts; the transport stays the authority on giving
    /// up, and "bounded" has to keep meaning something.
    /// </summary>
    private static readonly TimeSpan TransportPipelineCeiling = TimeSpan.FromSeconds(10);

    /// <summary>The seats that existed when this test was written. The positive control.</summary>
    private static readonly string[] KnownSeats =
    {
        "OfferPushNotifier.PushTimeout",
        "ChatMessagePushNotifier.PushTimeout",
        "NewRequestPushNotifier.PushTimeout",
        "DeliveryStatusPushNotifier.PerRecipientTimeout",
        "DispatchingRequestExpiryNotifier.PushTimeout",
        "ServiceCallbacksController.PushTimeout",
        "NewRequestFanoutOptions.PerSendTimeout",
    };

    /// <summary>
    /// Every static <see cref="TimeSpan"/> in the gateway assembly whose name marks it as a
    /// per-send push cap, plus the fan-out option's shipped default (an instance property, so
    /// it is read off a default-constructed options object).
    /// </summary>
    private static IReadOnlyList<(string Name, TimeSpan Value)> DiscoverPerSendCaps()
    {
        var capNames = new[] { "PushTimeout", "PerSendTimeout", "PerRecipientTimeout" };
        var found = new List<(string, TimeSpan)>();

        foreach (var type in typeof(OfferPushNotifier).Assembly.GetTypes())
        {
            var fields = type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            foreach (var f in fields.Where(f => f.FieldType == typeof(TimeSpan) && capNames.Contains(f.Name)))
            {
                found.Add(($"{type.Name}.{f.Name}", (TimeSpan)f.GetValue(null)!));
            }
        }

        // The fan-out's per-recipient cap is a bindable option, not a static field, so its
        // shipped default has to be read off a default-constructed instance. It is the seat
        // that fans a new request out to every jeeber, so leaving it out would leave the
        // widest-blast-radius copy unguarded.
        found.Add(("NewRequestFanoutOptions.PerSendTimeout", new NewRequestFanoutOptions().PerSendTimeout));

        return found;
    }

    /// <summary>
    /// POSITIVE CONTROL. A reflective scan that finds nothing passes every assertion below
    /// while proving nothing at all — the exact shape of a checker that certifies its own
    /// blindness. This fails loudly if a seat is renamed, moved, or made non-static, which is
    /// a prompt to re-point the scan rather than to enjoy a green run.
    /// </summary>
    [Fact]
    public void Discovery_Finds_Every_Known_Push_Seat()
    {
        var discovered = DiscoverPerSendCaps().Select(c => c.Name).ToArray();

        discovered.Should().Contain(KnownSeats,
            "a reflective budget scan that stops seeing the seats it was written for is a dead "
            + "instrument, and a dead instrument here reports PASS");
    }

    /// <summary>
    /// THE REGRESSION. Fails on the shipped tree before this change: five of the seven seats
    /// were 2s and 2s &lt; 3.969s.
    /// </summary>
    [Fact]
    public void Every_Per_Send_Cap_Clears_The_Measured_Healthy_Call()
    {
        var tooTight = DiscoverPerSendCaps()
            .Where(c => c.Value <= MeasuredWorstHealthyCall)
            .Select(c => $"{c.Name}={c.Value.TotalSeconds:0.###}s")
            .ToArray();

        tooTight.Should().BeEmpty(
            "a per-recipient cap at or below the slowest measured healthy call ({0}s) does not "
            + "bound a slow push service — it aborts every push to a recipient who actually "
            + "owns a device, which is the only kind of push that matters",
            MeasuredWorstHealthyCall.TotalSeconds);
    }

    /// <summary>
    /// The other half of "keep a bound". An unbounded push call blocks whatever hosts it, and
    /// a cap above the transport's own timeout is a deadline that can never fire.
    /// </summary>
    [Fact]
    public void No_Per_Send_Cap_Exceeds_The_Transport_Pipelines_Own_Timeout()
    {
        foreach (var (name, value) in DiscoverPerSendCaps())
        {
            value.Should().BeLessThanOrEqualTo(TransportPipelineCeiling,
                "{0} above the push pipeline's own {1}s timeout is a deadline the pipeline "
                + "always pre-empts — the bound would exist only on paper",
                name, TransportPipelineCeiling.TotalSeconds);
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL for the assertion above. The 2s value five seats actually shipped must
    /// FAIL the floor — otherwise the floor is satisfied for some reason unrelated to the fix
    /// and would have gone green on the defect it claims to catch.
    /// </summary>
    [Fact]
    public void The_Shipped_2s_Cap_Would_Have_Failed_The_Floor()
    {
        var shipped = TimeSpan.FromSeconds(2);

        shipped.Should().BeLessThan(MeasuredWorstHealthyCall,
            "if 2s ever stops being below the measured healthy call, the floor assertion has "
            + "lost its power and this whole file is decorative");
    }

    /// <summary>
    /// The detached ceiling must cover EVERY recipient's full budget. A flat ceiling
    /// re-imposes, from outside the notifier, exactly the shared deadline the per-recipient
    /// budget exists to prevent — and the recipient it starves is always the last one composed,
    /// which on both offer and delivery seats is the jeeber.
    /// </summary>
    [Theory]
    [InlineData(1)]   // winner only
    [InlineData(2)]   // winner + one losing bidder
    [InlineData(5)]
    public void Detached_Ceiling_Covers_Every_Recipients_Full_Budget(int recipients)
    {
        PushSendBudget.ForFanOut(recipients).Should().BeGreaterThan(
            PushSendBudget.PerRecipient * recipients,
            "a ceiling shorter than recipients x the per-recipient budget silently truncates "
            + "the fan-out and drops the LAST recipient composed");

        // The delivery seat's own ceiling derives from the same arithmetic; pinned here so the
        // two cannot drift apart again.
        DeliveriesController.DetachedPushBudgetFor(recipients)
            .Should().Be(PushSendBudget.ForFanOut(recipients));
    }

    /// <summary>
    /// Why raising the cap REQUIRED detaching the offer seats, expressed as arithmetic rather
    /// than as a comment nobody re-derives.
    ///
    /// <para>The offer-accept seats used to await winner + N losers inline, in front of the
    /// customer's accept 200. At the budget a push actually needs, that inline shape outlasts
    /// the mobile client's 15s receive timeout from the second recipient onward — and the
    /// accept has already committed, so the user is told "No internet connection" about an
    /// auction they successfully closed (JEBV4-281's failure, on a worse surface). This test
    /// exists so that anyone who moves the await back in front of a response has to delete an
    /// assertion that spells out the consequence.</para>
    /// </summary>
    [Fact]
    public void An_Inline_Accept_Fanout_At_This_Budget_Would_Outlast_The_Mobile_Receive_Timeout()
    {
        var mobileReceiveTimeout = TimeSpan.FromSeconds(15);
        var winnerPlusOneLoser = PushSendBudget.PerRecipient * 2;

        winnerPlusOneLoser.Should().BeGreaterThan(mobileReceiveTimeout,
            "this is the reason the offer seats dispatch through IDetachedPushDispatcher "
            + "instead of awaiting; if this ever stops holding, re-derive the trade before "
            + "moving the await back onto the request path");
    }

    /// <summary>
    /// The structural half of the assertion above: every offer seat must hold the detached
    /// dispatcher, and the accept-lifecycle fan-out must return <c>void</c> — a method that
    /// returns <c>void</c> cannot be awaited, which is the compiler enforcing the invariant
    /// the arithmetic above only explains. The seat that regressed shipped as
    /// <c>private async Task DispatchAcceptLifecyclePushesAsync(...)</c> awaited on the
    /// request path; restoring that shape has to fail here.
    ///
    /// <para><b>Stated ceiling.</b> This proves the seam is present and the fan-out is
    /// unawaitable. It does not prove every push on every branch goes through the seam — a
    /// new inline <c>await _offerPush...</c> elsewhere in these controllers would not be seen.
    /// Structure, not behaviour.</para>
    /// </summary>
    [Theory]
    [InlineData(typeof(OffersController), "DispatchAcceptLifecyclePushes")]
    [InlineData(typeof(JeebGateway.Controllers.V1.JeebOffersController), "DispatchAcceptLifecyclePushes")]
    [InlineData(typeof(RequestOffersController), null)]
    public void Offer_Seats_Dispatch_Behind_The_Response(Type controller, string? fanOutMethod)
    {
        controller
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(f => f.FieldType)
            .Should().Contain(typeof(IDetachedPushDispatcher),
                "{0} sends a push at a budget that must not sit in front of its response",
                controller.Name);

        if (fanOutMethod is null)
        {
            return;
        }

        var method = controller.GetMethod(
            fanOutMethod, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        method.Should().NotBeNull("the accept-lifecycle fan-out is the seat this change moved");
        method!.ReturnType.Should().Be(typeof(void),
            "a fan-out that returns Task is a fan-out somebody can await back onto the request "
            + "path; void is the compiler holding the line");
    }

    // ── The log line ──────────────────────────────────────────────────────────────
    //
    // A success line emitted for something that did not demonstrably happen is the same
    // failure mode as a refund client returning success for money that never moved, and it
    // cost three investigations. The seats used to log a bare "ACCEPTED by push service" and
    // discard the response body; these two messages are the REAL bodies measured on
    // 2026-07-28 behind the 201s that produced that line.

    [Theory]
    [InlineData("Notifications sent successfully to 4 device(s) for user u (15 failed)", 4, 15, 19)]
    [InlineData("Notifications sent successfully to 3 device(s) for user u (21 failed)", 3, 21, 24)]
    [InlineData("Notifications sent successfully to 2 device(s) for user u", 2, null, null)]
    public void Acceptance_Parses_The_Push_Services_Real_Device_Row_Accounting(
        string message, int accepted, int? failed, int? total)
    {
        var parsed = PushAcceptance.Parse(new SentPayloadResponse { Message = message });

        parsed.Accepted.Should().Be(accepted);
        parsed.Failed.Should().Be(failed);
        parsed.Total.Should().Be(total);
    }

    /// <summary>
    /// THE LOG REGRESSION. The rendered line must carry the counts, and must not be readable
    /// as "the recipient got it" — a 201 means FCM took at least one of the user's device
    /// rows, and on the live accounts 15 of 19 and 21 of 24 of those rows are dead.
    /// </summary>
    [Fact]
    public void Acceptance_Renders_The_Counts_And_Never_An_Unqualified_Success()
    {
        var rendered = PushAcceptance.Describe(new SentPayloadResponse
        {
            Message = "Notifications sent successfully to 4 device(s) for user u (15 failed)",
        });

        rendered.Should().Contain("4/19", "the reader needs the denominator, not the word ACCEPTED");
        rendered.Should().Contain("fcmRejected=15", "15 dead rows is the fact that explains the latency");
        rendered.Should().NotContainEquivalentOf("delivered",
            "nothing on this path knows whether any handset displayed anything");

        // "accepted" may appear ONLY as a labelled count (fcmAccepted=4/19), never as a
        // free-standing verdict. \b does not match inside "fcmAccepted", so this permits the
        // qualified form and rejects the bare word — which is precisely the difference
        // between the line this change ships and the line it replaces
        // ("Chat push ACCEPTED by push service for recipient …"), and precisely what three
        // consecutive investigations read as proof of delivery.
        Regex.IsMatch(rendered, @"\baccepted\b", RegexOptions.IgnoreCase)
            .Should().BeFalse(
                "a bare success word makes a claim this path cannot support; the count must "
                + "carry it. Rendered: {0}", rendered);
    }

    /// <summary>
    /// Unknown must render as unknown. A parser that silently renders a confident-looking
    /// <c>0</c> when the push service changes its wording reintroduces the same lie in a new
    /// costume.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("ok")]
    public void Acceptance_Says_Unknown_Rather_Than_Manufacturing_A_Zero(string message)
    {
        var rendered = PushAcceptance.Describe(new SentPayloadResponse { Message = message });

        rendered.Should().Contain("unknown");
        PushAcceptance.Parse(new SentPayloadResponse { Message = message }).Accepted.Should().BeNull();
    }
}
