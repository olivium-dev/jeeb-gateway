using FluentAssertions;
using JeebGateway.Notifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// NOT-02 (Domain 12) — the in-app inbox deep-link mapping is gateway-owned. These tests
/// pin the type→route resolution so a new upstream notification type can never silently
/// degrade an inbox row to a broken or template-literal link.
/// </summary>
public class NotificationDeepLinkResolverTests
{
    [Theory]
    // jeeb.* keys
    [InlineData("jeeb.delivery_status_updated", "del_9", "jeeb://deliveries/del_9/tracking")]
    [InlineData("jeeb.offer_received", "off_1", "jeeb://offers/off_1")]
    [InlineData("jeeb.offer_accepted", "off_2", "jeeb://offers/off_2")]
    [InlineData("jeeb.settlement_paid", "set_3", "jeeb://wallet/settlements/set_3")]
    [InlineData("jeeb.dispute_resolved", "dsp_4", "jeeb://disputes/dsp_4")]
    // bare upstream type aliases
    [InlineData("order_status", "del_5", "jeeb://deliveries/del_5/tracking")]
    [InlineData("request_expiry", "req_6", "jeeb://requests/req_6")]
    public void Resolves_Entity_Routes_With_Id(string type, string entityId, string expected)
    {
        NotificationDeepLinkResolver.Resolve(type, entityId).Should().Be(expected);
    }

    [Theory]
    // KYC has a fixed destination with no {id} token — resolves regardless of id presence.
    [InlineData("jeeb.kyc_approved", null, "jeeb://kyc/status")]
    [InlineData("jeeb.kyc_rejected", "", "jeeb://kyc/status")]
    [InlineData("kyc_approved", "ignored", "jeeb://kyc/status")]
    public void Resolves_Fixed_Routes_Without_Id(string type, string? entityId, string expected)
    {
        NotificationDeepLinkResolver.Resolve(type, entityId).Should().Be(expected);
    }

    [Fact]
    public void Is_Case_Insensitive_On_Type()
    {
        NotificationDeepLinkResolver.Resolve("JEEB.OFFER_RECEIVED", "off_x")
            .Should().Be("jeeb://offers/off_x");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme.unknown_type")]
    public void Unknown_Or_Blank_Type_Falls_Back_To_Inbox_Root(string? type)
    {
        NotificationDeepLinkResolver.Resolve(type, "anything")
            .Should().Be(NotificationDeepLinkResolver.InboxRoot);
    }

    [Fact]
    public void Entity_Route_Without_Id_Never_Emits_Template_Literal()
    {
        // A type that needs an id but has none must not produce "jeeb://offers/{id}".
        var link = NotificationDeepLinkResolver.Resolve("jeeb.offer_received", entityId: null);
        link.Should().Be(NotificationDeepLinkResolver.InboxRoot);
        link.Should().NotContain("{id}");
    }
}
