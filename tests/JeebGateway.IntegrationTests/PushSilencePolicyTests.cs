using System.Linq;
using System.Reflection;
using FluentAssertions;
using JeebGateway.Notifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// b02 step 3 — guards on the silent-vs-stored policy itself.
///
/// <para>The corollary these tests protect: a change that is BOTH worth telling the user
/// about AND requires a UI refresh is ONE non-silent stored notification whose data block
/// also carries the refresh category — NOT two pushes. Two is how you get a duplicated
/// shade. The enforcement is structural (one type ⇒ exactly one mode), and
/// <see cref="EveryCatalogTemplate_HasAnExplicitMode"/> is what stops a new notification
/// type from slipping past that decision.</para>
/// </summary>
public sealed class PushSilencePolicyTests
{
    // 2026-08-23 reversal: `newRequest` was the last silent-only category; its silent
    // data-only fan-out never rendered on-device, so jeebers missed new requests entirely.
    [Fact]
    public void NoCategoryIsSilent_AfterTheNewRequestReversal()
        => PushSilencePolicy.Categories
            .Where(category => PushSilencePolicy.ModeForCategory(category)
                == PushDeliveryMode.SilentRefresh)
            .Should().BeEmpty(
                "2026-08-23 reversal: `newRequest` moved to shade+stored because its "
                + "silent fan-out never rendered; any entry here silences a "
                + "human-addressed category and must be an explicit owner ruling");

    [Theory]
    // `newRequest` moved from the silent list on 2026-08-23 (measured: a silent fan-out
    // push never reaches the shade, so the reverse auction starved unnoticed).
    [InlineData(PushSilencePolicy.CategoryNewRequest)]
    // Moved from the silent list by the owner ruling of 2026-07-27: a delivery-status
    // change IS a readable inbox row. This row is what makes the
    // jeeb.delivery_status_updated centre writer legal.
    [InlineData(PushSilencePolicy.CategoryDelivery)]
    [InlineData(PushSilencePolicy.CategoryKyc)]
    [InlineData(PushSilencePolicy.CategorySettlement)]
    [InlineData(PushSilencePolicy.CategoryDispute)]
    [InlineData(PushSilencePolicy.CategoryRating)]
    [InlineData(PushSilencePolicy.CategoryChat)]
    // c4/W3 category for the `offer_withdrawn_insufficient_balance` push — a jeeber whose
    // offer was auto-withdrawn for balance must READ that, so it is never silent.
    [InlineData(PushSilencePolicy.CategoryWallet)]
    public void D4_ShadeAndStoredCategories_AreStored(string category)
        => PushSilencePolicy.ModeForCategory(category).Should()
            .Be(PushDeliveryMode.ShadeAndStored);

    [Fact]
    public void WalletCategory_IsStored()
        => PushSilencePolicy.ModeForCategory(PushSilencePolicy.CategoryWallet).Should()
            .Be(PushDeliveryMode.ShadeAndStored,
                "CONTRACT §3 freezes the guard-2 auto-withdraw push "
                + "(`offer_withdrawn_insufficient_balance`) as ShadeAndStored: exactly one "
                + "shade card + one notification-centre row, NEVER silent. Silencing "
                + "`wallet` would leave the jeeber believing they simply lost the auction "
                + "while their offer was pulled for balance — the c4 gap this epic closes");

    [Fact]
    public void NoCategoryIsBothModes()
    {
        // Single-valuedness is the corollary's enforcement: because ModeForCategory
        // returns ONE mode per category, no event can legally be emitted as a silent push
        // AND a stored push as two separate sends. Assert it as an observable property so a
        // future refactor to a multi-mode/flags shape fails here.
        foreach (var category in PushSilencePolicy.Categories)
        {
            var first = PushSilencePolicy.ModeForCategory(category);
            var second = PushSilencePolicy.ModeForCategory(category);

            second.Should().Be(first, $"'{category}' must resolve to one stable mode");
            first.Should().BeOneOf(
                PushDeliveryMode.SilentRefresh,
                PushDeliveryMode.ShadeAndStored);
        }
    }

    [Fact]
    public void EveryCatalogTemplate_HasAnExplicitMode()
        => PushSilencePolicy.TemplateKeys.Should().Contain(
            JeebNotificationCatalog.Keys,
            "adding a jeeb.* template without deciding silent-vs-stored must fail here "
            + "rather than default silently in production");

    [Fact]
    public void ThePolicyMapsNoTemplateKeyTheCatalogDoesNotDefine()
    {
        // The reverse direction of EveryCatalogTemplate_HasAnExplicitMode, and it catches a
        // failure mode that one cannot: a TYPO in a policy key. A mistyped entry here still
        // makes the map look total (same count), while the REAL key it was meant to cover
        // falls through to the unmapped default. For a type the owner ruled silent that
        // default is ShadeAndStored — i.e. the typo silently re-enables the row this policy
        // exists to prevent. Set equality is what closes it.
        PushSilencePolicy.TemplateKeys.Should().BeEquivalentTo(
            JeebNotificationCatalog.Keys,
            "the policy and the catalog must describe exactly the same set of notification "
            + "types; a key on one side only is either an undecided type or a typo");
    }

    [Fact]
    public void NoSilentClassifiedType_HasACentreWriteDto()
    {
        // ── THE §6a LANDMINE — IT FIRED, AND THE OWNER RULED ──────────────────────────
        // History, because a guard that has already done its job is the one people delete.
        // D4 (2026-07-26) said `delivery` is silent ⇒ jeeb.delivery_status_updated writes NO
        // row. Work order 6a wanted a jeeb.delivery_status_updated centre writer with a
        // "readable row per type" DoD. Those CONTRADICTED, and this test existed to force
        // the decision at the moment a writer DTO appeared rather than surfacing days later
        // as a confusing "6a is done but the rows are missing".
        //
        // It worked. Step 6 added DeliveryStatusUpdatedNotificationRecord and this test went
        // RED naming jeeb.delivery_status_updated. The owner then RULED (2026-07-27):
        // delivery IS a readable inbox row, shade + stored. The assertion below is UNCHANGED
        // — it is green because PushSilencePolicy no longer classifies delivery as silent,
        // NOT because the check was relaxed. Weakening it would defeat the whole mechanism.
        //
        // Reflection basis: a notification type gets a centre row only via a wire DTO that
        // declares `public const string TemplateKey` (JeebNotificationRecordDtos.cs). A new
        // writer needs a new DTO, so a new DTO is the earliest observable signal.
        var centreWriteDtoKeys = typeof(PushSilencePolicy).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "JeebGateway.Notifications")
            .Select(type => type.GetField(
                "TemplateKey",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string))
            .Select(field => (string)field!.GetRawConstantValue()!)
            .Distinct()
            .ToArray();

        // Guard the guard. A reflection query that matches nothing would make the assertion
        // below pass vacuously — the exact shape of failure this batch keeps being burned
        // by. STRENGTHENED on 2026-07-27: the pin now includes the delivery DTO whose
        // arrival made this test fire. That is deliberate. The whole point of the ruling is
        // that jeeb.delivery_status_updated HAS a centre writer, so if the reflection basis
        // ever stops seeing that DTO the assertion below would go green for the wrong
        // reason — the same green it would show if delivery had simply been re-silenced.
        centreWriteDtoKeys.Should().Contain(
            new[]
            {
                OfferReceivedNotificationRecord.TemplateKey,
                OfferAcceptedNotificationRecord.TemplateKey,
                DeliveryStatusUpdatedNotificationRecord.TemplateKey,
            },
            "if the reflection basis stops finding the known centre-write DTOs then this "
            + "test is passing because it looked nowhere, not because nothing is wrong");

        centreWriteDtoKeys.Where(PushSilencePolicy.IsSilent).Should().BeEmpty(
            "a type classified SilentRefresh must have NO notification-centre write DTO — "
            + "its rows would be POSTed and then discarded by the gate. This is the check "
            + "that forced the delivery decision in b02: it went red when step 6 added "
            + "DeliveryStatusUpdatedNotificationRecord, and the owner resolved it on "
            + "2026-07-27 by ruling delivery shade+stored. If it is red again, some type "
            + "has both a centre writer and a silent classification — resolve THAT, do not "
            + "delete this test and do not delete the DTO");
    }

    [Fact]
    public void UnknownInputs_FailTowardsTheVisibleOutcome()
    {
        // Not symmetric failure modes: a surplus inbox row is a visible, correctable
        // cosmetic bug; a wrongly-silenced human notification is invisible data loss.
        PushSilencePolicy.ModeForCategory("not-a-category").Should()
            .Be(PushDeliveryMode.ShadeAndStored);
        PushSilencePolicy.ModeForTemplateKey("jeeb.not_a_template").Should()
            .Be(PushDeliveryMode.ShadeAndStored);
        PushSilencePolicy.ModeForCategory(null).Should()
            .Be(PushDeliveryMode.ShadeAndStored);
        PushSilencePolicy.IsSilent(null).Should().BeFalse();
    }

    /// <summary>
    /// Owner ruling 2026-07-27, stated as the single assertion the four already-merged read
    /// paths depend on. <c>JeebNotificationsInboxController.PayloadRef</c>,
    /// <see cref="NotificationDeepLinkResolver"/>, <see cref="JeebNotificationCatalog"/> and
    /// <see cref="JeebNotificationCatalogSeeder"/> all assume a
    /// <c>jeeb.delivery_status_updated</c> row can exist. If this flips back to silent, all
    /// four become unreachable code and the step-6a writer starts POSTing into the gate.
    /// </summary>
    [Fact]
    public void DeliveryStatusUpdated_IsStored_SoTheInboxReadPathsAreReachable()
    {
        PushSilencePolicy.IsSilent("jeeb.delivery_status_updated").Should().BeFalse(
            "owner ruling 2026-07-27 REVERSED D4 for this type: a delivery-status change "
            + "is a readable inbox row, shade AND stored");
        PushSilencePolicy.ModeForTemplateKey("jeeb.delivery_status_updated").Should()
            .Be(PushDeliveryMode.ShadeAndStored);
        PushSilencePolicy.CategoryForTemplateKey("jeeb.delivery_status_updated").Should()
            .Be(PushSilencePolicy.CategoryDelivery,
                "the type still carries the `delivery` refresh category — the corollary "
                + "says the refresh rides in the stored push's data block, it does not "
                + "become a second silent push");
    }

    [Theory]
    [InlineData("jeeb.offer_received")]
    [InlineData("jeeb.offer_accepted")]
    [InlineData("jeeb.offer_updated")]
    public void OfferLifecycleCallbacks_StayNonSilent(string templateKey)
        => PushSilencePolicy.IsSilent(templateKey).Should().BeFalse(
            "offer lifecycle updates are visible user notifications");

    [Fact]
    public void OfferUpdated_MapsToItsVisibleOfferLifecycleCategory()
    {
        PushSilencePolicy.CategoryForTemplateKey("jeeb.offer_updated").Should()
            .Be(PushSilencePolicy.CategoryOfferUpdated);
        PushSilencePolicy.ModeForTemplateKey("jeeb.offer_updated").Should()
            .Be(PushDeliveryMode.ShadeAndStored);
    }

    [Fact]
    public void NoCatalogTypeIsSilentToday_AndTheGateIsKeptAnyway()
        // The honest post-reversal state, pinned so nobody infers it from a green suite.
        // Every catalog template key AND every category is now ShadeAndStored (2026-08-23),
        // so NotificationRecordWriter's silent gate suppresses nothing in production.
        // That makes the gate dormant, which is the intended state of a guard, NOT dead
        // code to delete. The day a silent type is introduced, someone must come here.
        => PushSilencePolicy.TemplateKeys.Where(PushSilencePolicy.IsSilent).Should().BeEmpty(
            "after the 2026-07-27 reversal no catalog notification type is silent; if this "
            + "is red you have added one, and you must also add a writer-level test proving "
            + "the gate actually suppresses it");

    [Fact]
    public void TheLegacyFlatCategoryField_IsNotARefreshCategory()
    {
        // OfferPushNotifier.cs and NewRequestPushNotifier.cs both stamp
        // ["category"] = "delivery" on the wire — a coarse product-area label, NOT the D4
        // taxonomy. Before 2026-07-27 that literal collided with a SILENT category and
        // resolving the mode from the field would have silenced the offer pushes. The
        // reversal made the collision harmless BY ACCIDENT, which is worse than the old
        // trap, not better: the wrong-by-design lookup now returns the right answer, so a
        // reader can no longer discover the bug by running it. Pinned deliberately.
        PushSilencePolicy.ModeForCategory(PushSilencePolicy.CategoryDelivery).Should()
            .Be(PushDeliveryMode.ShadeAndStored,
                "reversed 2026-07-27 — this used to be SilentRefresh");
        PushSilencePolicy.ModeForTemplateKey("jeeb.offer_received").Should()
            .Be(PushDeliveryMode.ShadeAndStored, "resolved from the TYPE, not that field");
    }
}
