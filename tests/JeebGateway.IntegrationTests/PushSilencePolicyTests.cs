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
    // Owner ruling D4, 2026-07-26.
    [Theory]
    [InlineData(PushSilencePolicy.CategoryNewRequest)]
    [InlineData(PushSilencePolicy.CategoryDelivery)]
    public void D4_SilentOnlyCategories_AreSilent(string category)
        => PushSilencePolicy.ModeForCategory(category).Should()
            .Be(PushDeliveryMode.SilentRefresh);

    [Theory]
    [InlineData(PushSilencePolicy.CategoryKyc)]
    [InlineData(PushSilencePolicy.CategorySettlement)]
    [InlineData(PushSilencePolicy.CategoryDispute)]
    [InlineData(PushSilencePolicy.CategoryRating)]
    [InlineData(PushSilencePolicy.CategoryChat)]
    public void D4_ShadeAndStoredCategories_AreStored(string category)
        => PushSilencePolicy.ModeForCategory(category).Should()
            .Be(PushDeliveryMode.ShadeAndStored);

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
        // ── THE §6a LANDMINE, ENFORCED RATHER THAN COMMENTED ───────────────────────────
        // Owner ruling D4 says `delivery` is silent ⇒ jeeb.delivery_status_updated writes
        // NO row. Work order 6a wants a jeeb.delivery_status_updated centre writer with a
        // "readable row per type" DoD. Those CONTRADICT. It is inert today because no
        // writer exists — so the failure would otherwise surface as a confusing "6a is
        // done but the rows are missing", days later, to someone who never read the
        // comment. This test makes it surface at the moment the writer is added instead.
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
        // by. Pin the two DTOs that provably exist today, so the query must still be
        // finding them for the real assertion to mean anything.
        centreWriteDtoKeys.Should().Contain(
            new[] { OfferReceivedNotificationRecord.TemplateKey, OfferAcceptedNotificationRecord.TemplateKey },
            "if the reflection basis stops finding the two known centre-write DTOs then "
            + "this test is passing because it looked nowhere, not because nothing is wrong");

        centreWriteDtoKeys.Where(PushSilencePolicy.IsSilent).Should().BeEmpty(
            "a type classified SilentRefresh must have NO notification-centre write DTO — "
            + "its rows would be POSTed and then discarded by the gate. If this is red "
            + "because step 6a added a writer for jeeb.delivery_status_updated: owner "
            + "ruling D4 (delivery = silent, no row) and work order 6a (delivery_status "
            + "needs a readable row) contradict each other. That is an OWNER DECISION. Do "
            + "not resolve it by deleting this test or by flipping the policy row");
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

    [Fact]
    public void DeliveryStatusUpdated_IsSilent_AndSoWritesNoRow()
        => PushSilencePolicy.IsSilent("jeeb.delivery_status_updated").Should().BeTrue();

    [Theory]
    [InlineData("jeeb.offer_received")]
    [InlineData("jeeb.offer_accepted")]
    public void TheTwoLiveCentreWriters_StayNonSilent(string templateKey)
        => PushSilencePolicy.IsSilent(templateKey).Should().BeFalse(
            "these are the only notification types with a live centre writer; silencing "
            + "them would delete the gateway's entire inbox output");

    [Fact]
    public void TheLegacyFlatCategoryField_IsNotARefreshCategory()
    {
        // OfferPushNotifier.cs and NewRequestPushNotifier.cs both stamp
        // ["category"] = "delivery" on the wire — a coarse product-area label, NOT the D4
        // taxonomy. Resolving the mode from that field would silence the offer pushes. This
        // test pins the trap so the next reader sees it stated, not inferred.
        PushSilencePolicy.ModeForCategory(PushSilencePolicy.CategoryDelivery).Should()
            .Be(PushDeliveryMode.SilentRefresh);
        PushSilencePolicy.ModeForTemplateKey("jeeb.offer_received").Should()
            .Be(PushDeliveryMode.ShadeAndStored, "resolved from the TYPE, not that field");
    }
}
