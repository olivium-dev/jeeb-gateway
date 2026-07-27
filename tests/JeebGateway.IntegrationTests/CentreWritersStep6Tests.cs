using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// b02 step 6 — the notification-centre writers (6a) and the retirement of the unroutable
/// <c>jeeb.offer_rejected</c> taxonomy (6b).
///
/// <para><b>What these tests are for, and what they are NOT.</b> They pin the gateway-side
/// contract: the right path is POSTed, the right closed payload is serialized, and the silent gate
/// still owns the decision. They CANNOT prove a row is readable from the notification centre —
/// only a live probe against <c>:10026</c> can, and the step-6 DoD is discharged there. What these
/// catch is the class of regression a live probe would miss weeks later: a renamed JSON field, a
/// writer quietly bypassing <see cref="NotificationRecordWriter"/>, or the retired type creeping
/// back into the catalog.</para>
/// </summary>
[Collection("FM1 notification durability telemetry")]
public sealed class CentreWritersStep6Tests
{
    private const string Recipient = "11111111-2222-3333-4444-555555555555";

    // ─────────────────────────────────────────────────────────────────────────────────────
    // 6a — the writers
    // ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All SIX step-6a types each POST exactly once to their own centre path.
    ///
    /// <para>The path is asserted, not a counter. Every one of these routes was probed live on the
    /// centre (422 for an empty body ⇒ the route exists); a typo'd path would answer 404 in
    /// production while a bare "one POST happened" assertion stayed green.</para>
    ///
    /// <para><b>2026-07-27 — <c>jeeb.delivery_status_updated</c> joined this list.</b> It used to
    /// be the step's negative case, asserting ZERO POSTs because D4 classified <c>delivery</c> as
    /// silent. The owner reversed that: delivery IS a readable inbox row. Six writers, six rows.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("jeeb.delivery_status_updated")]
    [InlineData("jeeb.settlement_paid")]
    [InlineData("jeeb.kyc_approved")]
    [InlineData("jeeb.kyc_rejected")]
    [InlineData("jeeb.dispute_resolved")]
    [InlineData("jeeb.rating_auto_revealed")]
    public async Task Step6a_StoredTypes_PostOnceToTheirOwnCentrePath(string templateKey)
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        var outcome = await WriteAsync(writer, templateKey);

        outcome.Classification.Should().Be(NotificationRecordWriteClassification.Committed);
        handler.Posts.Should().Be(1, "one emission is one POST — the writer never retries");
        handler.PostPaths.Single().Should().Be($"/notifications/{templateKey}");
    }

    /// <summary>
    /// <b>THE REVERSAL, PINNED — this test used to assert the exact opposite.</b>
    ///
    /// <para>Until 2026-07-27 this was <c>Step6a_SilentType_WritesNoRowAndIssuesNoPost</c>: it
    /// asserted <c>SkippedSilent</c> and ZERO POSTs, because owner ruling D4 (2026-07-26) put the
    /// <c>delivery</c> category on the silent side while work order 6a demanded a readable
    /// <c>jeeb.delivery_status_updated</c> row. That contradiction is what the reflection landmine
    /// <c>PushSilencePolicyTests.NoSilentClassifiedType_HasACentreWriteDto</c> was built to force,
    /// and it did: adding <see cref="DeliveryStatusUpdatedNotificationRecord"/> turned it red. The
    /// owner then ruled — <b>delivery IS a readable inbox row, shade + stored</b>.</para>
    ///
    /// <para>So the row is now written, and this test states that as a behaviour rather than as a
    /// policy-table lookup. It is kept as its own <c>[Fact]</c>, separate from the six-type Theory
    /// above, purely so the reversal has a named home a reader can find. The transport was never
    /// the obstacle: this route answers 422 for an empty body like the other five, so the only
    /// thing that ever suppressed it was <see cref="PushSilencePolicy"/>.</para>
    /// </summary>
    [Fact]
    public async Task Step6a_DeliveryStatusUpdated_WritesARowAfterThe20260727Reversal()
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        var outcome = await WriteAsync(writer, DeliveryStatusUpdatedNotificationRecord.TemplateKey);

        outcome.Classification.Should().Be(
            NotificationRecordWriteClassification.Committed,
            "owner ruling 2026-07-27 REVERSED D4 for delivery: it is a readable inbox row. A "
            + "SkippedSilent here means the policy row was flipped back and the inbox read paths "
            + "in JeebNotificationsInboxController / NotificationDeepLinkResolver are now dead");
        handler.Posts.Should().Be(1, "one emission is one POST — the writer never retries");
        handler.PostPaths.Single().Should()
            .Be($"/notifications/{DeliveryStatusUpdatedNotificationRecord.TemplateKey}");
    }

    /// <summary>
    /// The durable-write flag and the silent gate are distinct conditions and must not be
    /// confusable: switching the flag off reports <c>Disabled</c>, never <c>SkippedSilent</c>. The
    /// converse half of this pair — a silent type skipped even with durable write fully enabled —
    /// has no reachable type since the 2026-07-27 reversal (see the note in
    /// <c>NotificationRecordWriterTests</c>), so this direction is what remains live.
    /// </summary>
    [Fact]
    public async Task Step6a_DisabledFlag_IsReportedAsDisabledNotAsSilent()
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler, enabled: false);

        var outcome = await WriteAsync(writer, SettlementPaidNotificationRecord.TemplateKey);

        outcome.Classification.Should().Be(NotificationRecordWriteClassification.Disabled);
        handler.Posts.Should().Be(0);
    }

    /// <summary>
    /// The closed payloads must serialize with the centre's snake_case field names and with numbers
    /// as JSON numbers.
    ///
    /// <para>Both halves come from the live schema: <c>settlement_paidPayload</c> requires
    /// <c>payment_amount</c> as <c>type: number</c>. A camelCase field or a stringified amount is
    /// a 422 from the centre — which the gateway's single-attempt writer classifies as "unproven"
    /// and never retries, so the row is simply lost. That failure is invisible in the gateway's
    /// response to its caller, which is why it is pinned here.</para>
    /// </summary>
    [Fact]
    public async Task Step6a_SettlementPayload_UsesSnakeCaseAndJsonNumbers()
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        await writer.WriteSettlementPaidAsync(
            new SettlementPaidNotificationRecord
            {
                Sender = ServiceCallbackRecordFactory.Sender,
                Receiver = Recipient,
                NotificationCorrelationId = Guid.NewGuid().ToString("D"),
                Title = "Payment Completed",
                Description = "body",
                NotificationType = SettlementPaidNotificationRecord.TemplateKey,
                Payload = new SettlementPaidNotificationPayload
                {
                    UserId = Recipient,
                    SettlementId = "settle-1",
                    PaymentAmount = 12.5m,
                    Currency = "USD",
                    PaymentMethod = "cash",
                    TransactionId = "txn-1",
                    CreatedAt = DateTimeOffset.Parse("2026-07-26T10:11:12Z"),
                },
            },
            CancellationToken.None);

        var root = JsonDocument.Parse(handler.PostBodies.Single()).RootElement;
        root.GetProperty("notification_type").GetString()
            .Should().Be(SettlementPaidNotificationRecord.TemplateKey);
        // The read-back that classifies an ambiguous POST matches on this exact field name.
        root.TryGetProperty("notification_id", out _).Should().BeTrue();
        root.GetProperty("subtitle").GetString().Should().BeEmpty("the centre requires it, we do not invent it");

        var payload = root.GetProperty("payload");
        payload.GetProperty("payment_amount").ValueKind.Should().Be(JsonValueKind.Number);
        payload.GetProperty("payment_amount").GetDecimal().Should().Be(12.5m);
        payload.GetProperty("settlement_id").GetString().Should().Be("settle-1");
        payload.GetProperty("payment_method").GetString().Should().Be("cash");
        payload.GetProperty("transaction_id").GetString().Should().Be("txn-1");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    // 6a — the callback→record mapping
    // ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ServiceCallbackRecordFactory.CanWrite"/> covers exactly the six step-6a types.
    /// The two offer types must answer false: they already have live writers at their in-gateway
    /// seats, and the centre does not deduplicate on <c>notification_id</c>, so a second producer
    /// means a second inbox row for one event.
    /// </summary>
    [Theory]
    [InlineData("jeeb.delivery_status_updated", true)]
    [InlineData("jeeb.settlement_paid", true)]
    [InlineData("jeeb.kyc_approved", true)]
    [InlineData("jeeb.kyc_rejected", true)]
    [InlineData("jeeb.dispute_resolved", true)]
    [InlineData("jeeb.rating_auto_revealed", true)]
    [InlineData("jeeb.offer_received", false)]
    [InlineData("jeeb.offer_accepted", false)]
    [InlineData("jeeb.offer_rejected", false)]
    [InlineData(null, false)]
    public void Step6a_CanWrite_CoversExactlyTheSixTypes(string? templateKey, bool expected)
        => ServiceCallbackRecordFactory.CanWrite(templateKey).Should().Be(expected);

    /// <summary>
    /// A caller's flat <c>data</c> map lands in the closed typed payload, in both the snake and
    /// camel spellings, and an omitted required string becomes the explicit absence sentinel rather
    /// than a blank that reads as real data in the inbox.
    /// </summary>
    [Fact]
    public async Task Step6a_Factory_MapsFlatDataAndMarksAbsentFieldsExplicitly()
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        var write = ServiceCallbackRecordFactory.WriteAsync(
            writer,
            DisputeResolvedNotificationRecord.TemplateKey,
            Recipient,
            new NotificationTemplate("Dispute Resolved", "body"),
            new Dictionary<string, string>
            {
                ["dispute_id"] = "dsp-1",
                ["orderId"] = "ord-1",             // camel spelling accepted
                ["resolution_amount"] = "4.25",
                ["created_at"] = "2026-07-26T10:11:12Z",
                // resolution_type, resolution_details and resolved_by deliberately omitted
            },
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        write.Should().NotBeNull();
        await write!;

        var payload = JsonDocument.Parse(handler.PostBodies.Single())
            .RootElement.GetProperty("payload");
        payload.GetProperty("dispute_id").GetString().Should().Be("dsp-1");
        payload.GetProperty("order_id").GetString().Should().Be("ord-1");
        payload.GetProperty("resolution_amount").GetDecimal().Should().Be(4.25m);
        payload.GetProperty("resolved_by").GetString().Should().Be(JeebNotificationCentre.Absent);
        payload.GetProperty("resolution_type").GetString().Should().Be(JeebNotificationCentre.Absent);
        payload.GetProperty("user_id").GetString().Should().Be(Recipient);
    }

    /// <summary>
    /// A decimal on the wire is parsed invariant-culture. A server locale that reads "4,25" as 425
    /// would inflate a settlement amount by two orders of magnitude, and nothing downstream would
    /// flag it — the row would simply claim the user was paid more than they were.
    /// </summary>
    [Fact]
    public async Task Step6a_Factory_ParsesAmountsInvariantCulture()
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        await ServiceCallbackRecordFactory.WriteAsync(
            writer,
            SettlementPaidNotificationRecord.TemplateKey,
            Recipient,
            new NotificationTemplate("Payment Completed", "body"),
            new Dictionary<string, string> { ["payment_amount"] = "1234.56" },
            Guid.NewGuid().ToString("D"),
            CancellationToken.None)!;

        JsonDocument.Parse(handler.PostBodies.Single())
            .RootElement.GetProperty("payload")
            .GetProperty("payment_amount").GetDecimal().Should().Be(1234.56m);
    }

    /// <summary>
    /// An omitted <c>resubmission_allowed</c> defaults to TRUE. Of the two possible defaults only
    /// this one is safe: wrongly telling a user they may resubmit is a correctable annoyance,
    /// wrongly telling them they may not is a dead end that loses a verified user.
    /// </summary>
    [Fact]
    public async Task Step6a_Factory_KycRejection_DefaultsToResubmissionAllowed()
    {
        var handler = new PathRecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        await ServiceCallbackRecordFactory.WriteAsync(
            writer,
            KycRejectedNotificationRecord.TemplateKey,
            Recipient,
            new NotificationTemplate("KYC Verification Required", "body"),
            new Dictionary<string, string> { ["required_documents"] = "id_front, id_back" },
            Guid.NewGuid().ToString("D"),
            CancellationToken.None)!;

        var payload = JsonDocument.Parse(handler.PostBodies.Single())
            .RootElement.GetProperty("payload");
        payload.GetProperty("resubmission_allowed").GetBoolean().Should().BeTrue();
        payload.GetProperty("required_documents").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo("id_front", "id_back");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    // 6b — the retirement
    // ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>jeeb.offer_rejected</c> is gone from the catalog and unreachable through it. The live
    /// centre answers <b>405</b> for <c>POST /notifications/jeeb.offer_rejected</c> where every
    /// served type answers 422, so no row of that type can exist and declaring it was unroutable
    /// taxonomy. Owner ruling D3 = retire.
    /// </summary>
    [Fact]
    public void Step6b_RetiredType_IsNotInTheCatalog()
    {
        JeebNotificationCatalog.HasTemplate("jeeb.offer_rejected").Should().BeFalse();
        JeebNotificationCatalog.Keys.Should().NotContain("jeeb.offer_rejected");
        JeebNotificationCatalog.All.Keys.Should().NotContain(
            "jeeb.offer_rejected",
            "the seeder iterates All, so a stale entry there would re-register it upstream");
    }

    /// <summary>
    /// The deep-link resolver no longer routes either spelling. This map exists to link an INBOX
    /// ROW, and there is no centre route for <c>jeeb.offer_rejected</c> OR the bare
    /// <c>offer_rejected</c> — both were probed — so an entry here could never be reached.
    /// </summary>
    [Theory]
    [InlineData("jeeb.offer_rejected")]
    [InlineData("offer_rejected")]
    public void Step6b_RetiredType_HasNoInboxDeepLink(string notificationType)
        => NotificationDeepLinkResolver.Resolve(notificationType, "offer-1")
            .Should().Be(NotificationDeepLinkResolver.InboxRoot);

    /// <summary>
    /// The point of the retirement that is easy to get wrong: the loser-bidder PUSH keeps its exact
    /// copy and its offer deep link. That push never needed the notification centre, so retiring an
    /// unroutable centre taxonomy must not degrade it into the catalog's product-neutral fallback
    /// ("You have a new notification for jeeb.offer_rejected"). The copy moved next to its only
    /// caller; this asserts it moved intact.
    /// </summary>
    [Fact]
    public void Step6b_LoserPushCopyAndDeepLink_SurvivedTheRetirement()
    {
        OfferPushNotifier.OfferLostTemplate.Title.Should().Be("Offer Not Selected");
        OfferPushNotifier.OfferLostTemplate.Body.Should()
            .Be("Your offer wasn't selected this time. Keep an eye out for new delivery requests.");
        OfferPushNotifier.OfferLostDeepLink("offer-9").Should().Be("jeeb://offers/offer-9");

        // And it is NOT reachable via the catalog any more — which is what would have silently
        // degraded the copy if it had been left to Render().
        JeebNotificationCatalog.Render("jeeb.offer_rejected").Title.Should().Be("Notification");
    }

    /// <summary>
    /// Every notification type the catalog still declares is one the centre serves and one this
    /// step can account for: eight keys, six with a step-6a writer plus the two offer types that
    /// write from their own seats. A ninth key appearing without a route is the exact defect
    /// correction C6 described, and it would land here first.
    /// </summary>
    [Fact]
    public void Step6b_CatalogIsExactlyTheEightServedTypes()
        => JeebNotificationCatalog.Keys.Should().BeEquivalentTo(new[]
        {
            "jeeb.offer_received",
            "jeeb.offer_accepted",
            "jeeb.delivery_status_updated",
            "jeeb.settlement_paid",
            "jeeb.kyc_approved",
            "jeeb.kyc_rejected",
            "jeeb.dispute_resolved",
            "jeeb.rating_auto_revealed",
        });

    // ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the writer through <see cref="ServiceCallbackRecordFactory"/> so the test exercises
    /// the SAME construction path production uses, rather than a hand-built record that could drift
    /// from it.
    /// </summary>
    private static Task<NotificationRecordWriteOutcome> WriteAsync(
        INotificationRecordWriter writer,
        string templateKey)
        => ServiceCallbackRecordFactory.WriteAsync(
            writer,
            templateKey,
            Recipient,
            new NotificationTemplate("title", "body"),
            new Dictionary<string, string> { ["entityId"] = "entity-1" },
            Guid.NewGuid().ToString("D"),
            CancellationToken.None)!;

    private static NotificationRecordWriter NewWriter(
        PathRecordingHandler handler,
        bool enabled = true)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = enabled.ToString(),
            })
            .Build();
        return new NotificationRecordWriter(
            new JeebNotificationRecordClient(http),
            configuration,
            NullLogger<NotificationRecordWriter>.Instance);
    }

    /// <summary>
    /// Records the PATH of every request, not just a count: the step-6a claim is "each type posts
    /// to its own centre route", and a counter cannot distinguish that from six posts to one route.
    /// </summary>
    private sealed class PathRecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _postStatus;

        public PathRecordingHandler(HttpStatusCode postStatus) => _postStatus = postStatus;

        public int Posts { get; private set; }
        public int Gets { get; private set; }
        public List<string> PostPaths { get; } = new();
        public List<string> PostBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                PostPaths.Add(request.RequestUri!.AbsolutePath);
                if (request.Content is not null)
                {
                    PostBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                }

                return new HttpResponseMessage(_postStatus);
            }

            Gets++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"messages":[],"total_messages":0}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
