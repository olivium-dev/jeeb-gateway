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
