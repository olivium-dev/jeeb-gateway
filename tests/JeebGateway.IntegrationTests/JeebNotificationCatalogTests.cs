using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Push;
using JeebGateway.Users;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1486 — the Jeeb notification taxonomy and the active_role → FCM topic
/// routing were RELOCATED from the shared services into the gateway. These
/// tests pin the gateway-owned catalog (<see cref="JeebNotificationCatalog"/>)
/// and topic map (<see cref="JeebPushTopicMap"/>) so the de-leak cannot regress.
/// </summary>
public class JeebNotificationCatalogTests
{
    public static readonly string[] ExpectedKeys =
    {
        "jeeb.offer_received",
        "jeeb.offer_accepted",
        "jeeb.delivery_status_updated",
        "jeeb.settlement_paid",
        "jeeb.kyc_approved",
        "jeeb.kyc_rejected",
        "jeeb.dispute_resolved",
        "jeeb.rating_auto_revealed",
    };

    [Fact]
    public void Catalog_Owns_The_Eight_Jeeb_Templates()
    {
        JeebNotificationCatalog.Keys.Should().BeEquivalentTo(ExpectedKeys);
    }

    [Theory]
    [InlineData("jeeb.offer_received", "en", "New Delivery Offer")]
    [InlineData("jeeb.offer_received", "ar", "عرض توصيل جديد")]
    [InlineData("jeeb.settlement_paid", "en", "Payment Completed")]
    [InlineData("jeeb.kyc_approved", "ar", "تم الموافقة على التحقق")]
    public void Render_Returns_Localized_Title(string key, string locale, string expectedTitle)
    {
        var template = JeebNotificationCatalog.Render(key, locale);
        template.Title.Should().Be(expectedTitle);
        template.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Render_Falls_Back_To_Default_Locale_For_Unsupported_Locale()
    {
        var en = JeebNotificationCatalog.Render("jeeb.offer_received", "en");
        var fr = JeebNotificationCatalog.Render("jeeb.offer_received", "fr");
        fr.Should().BeEquivalentTo(en);
    }

    [Fact]
    public void Render_Accepts_Region_Subtag()
    {
        var ar = JeebNotificationCatalog.Render("jeeb.offer_received", "ar");
        var arSa = JeebNotificationCatalog.Render("jeeb.offer_received", "ar-SA");
        arSa.Should().BeEquivalentTo(ar);
    }

    [Fact]
    public void Render_Substitutes_Known_Placeholders_And_Leaves_Unknown_Intact()
    {
        var template = JeebNotificationCatalog.Render(
            "jeeb.offer_received",
            "en",
            new Dictionary<string, string> { ["unused"] = "x" });

        // No placeholders in this template; output is unchanged and never throws.
        template.Title.Should().Be("New Delivery Offer");
    }

    [Fact]
    public void Render_Unknown_Key_Returns_Generic_Fallback()
    {
        var template = JeebNotificationCatalog.Render("acme.unknown", "en");
        template.Title.Should().Be("Notification");
        template.Body.Should().Contain("acme.unknown");
    }

    [Fact]
    public void All_Exposes_Every_Key_With_Both_Locales()
    {
        foreach (var key in ExpectedKeys)
        {
            JeebNotificationCatalog.HasTemplate(key).Should().BeTrue();
            var byLocale = JeebNotificationCatalog.All[key];
            byLocale.Keys.Should().Contain(new[] { "en", "ar" });
        }
    }
}

/// <summary>
/// JEB-1486 — active_role → FCM topic mapping is owned by the gateway; the push
/// relay only sees opaque topic strings.
/// </summary>
public class JeebPushTopicMapTests
{
    [Theory]
    [InlineData("customer")]   // opaque (user-management vocabulary)
    [InlineData("client")]     // frozen Jeeb contract role
    [InlineData("jeeb_client")] // legacy push role string
    [InlineData("CUSTOMER")]   // case-insensitive
    public void Client_Roles_Map_To_Clients_Topic(string role)
    {
        JeebPushTopicMap.TopicForRole(role).Should().Be(JeebPushTopicMap.ClientsTopic);
    }

    [Theory]
    [InlineData("driver")]
    [InlineData("jeeber")]
    [InlineData("jeeb_jeeber")]
    [InlineData("Driver")]
    public void Jeeber_Roles_Map_To_Jeebers_Topic(string role)
    {
        JeebPushTopicMap.TopicForRole(role).Should().Be(JeebPushTopicMap.JeebersTopic);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("admin")]
    [InlineData("unknown")]
    public void Unmapped_Roles_Return_Null(string? role)
    {
        JeebPushTopicMap.TopicForRole(role).Should().BeNull();
    }

    [Fact]
    public void Opaque_Role_Constants_Stay_Aligned_With_Roles()
    {
        // Guards against a silent drift between the gateway role vocabulary and
        // the topic map (customer/driver are the opaque UM roles).
        JeebPushTopicMap.TopicForRole(Roles.Client).Should().Be(JeebPushTopicMap.ClientsTopic);
        JeebPushTopicMap.TopicForRole(Roles.Jeeber).Should().Be(JeebPushTopicMap.JeebersTopic);
    }
}
