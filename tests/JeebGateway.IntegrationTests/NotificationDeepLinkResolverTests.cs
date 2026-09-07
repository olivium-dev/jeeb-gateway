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
    [InlineData("jeeb.delivery_status_updated", "del_9", "jeeb://orders/del_9")]
    [InlineData("jeeb.offer_received", "req_1", "jeeb://requests/req_1/offers")]
    [InlineData("jeeb.offer_accepted", "req_2", "jeeb://chat/req_2")]
    [InlineData("jeeb.offer_updated", "req_3", "jeeb://requests/req_3/offers")]
    [InlineData("offer_updated", "req_4", "jeeb://requests/req_4/offers")]
    [InlineData("jeeb.settlement_paid", "set_3", "jeeb://wallet")]
    [InlineData("jeeb.dispute_resolved", "dsp_4", "jeeb://disputes/dsp_4")]
    // bare upstream type aliases
    [InlineData("order_status", "del_5", "jeeb://orders/del_5")]
    [InlineData("request_expiry", "req_6", "jeeb://requests/req_6/waiting")]
    [InlineData("new_request", "req", "jeeb://jeeber/requests/req")]
    [InlineData("chat", "req", "jeeb://chat/req")]
    [InlineData("chat_message", "req", "jeeb://chat/req")]
    [InlineData("offer", "req", "jeeb://requests/req/offers")]
    [InlineData("delivery", "req", "jeeb://orders/req")]
    [InlineData("cancellation_decision", "req", "jeeb://orders/req")]
    [InlineData("request.try_expand_tier", "req", "jeeb://requests/req/waiting")]
    [InlineData("request.expired", "req", "jeeb://requests/req/waiting")]
    [InlineData("request_expiring", "req", "jeeb://requests/req/waiting")]
    [InlineData("offer_lost", "req", "jeeb://notifications")]
    [InlineData("availability", null, "jeeb://notifications")]
    public void Resolves_Entity_Routes_With_Id(string type, string? entityId, string expected)
    {
        NotificationDeepLinkResolver.Resolve(type, entityId).Should().Be(expected);
    }

    [Theory]
    // KYC has a fixed destination with no {id} token — resolves regardless of id presence.
    [InlineData("jeeb.kyc_approved", null, "jeeb://profile/kyc")]
    [InlineData("jeeb.kyc_rejected", "", "jeeb://profile/kyc")]
    [InlineData("kyc_approved", "ignored", "jeeb://profile/kyc")]
    [InlineData("settlement_paid", null, "jeeb://wallet")]
    public void Resolves_Fixed_Routes_Without_Id(string type, string? entityId, string expected)
    {
        NotificationDeepLinkResolver.Resolve(type, entityId).Should().Be(expected);
    }

    [Fact]
    public void Is_Case_Insensitive_On_Type()
    {
        NotificationDeepLinkResolver.Resolve("JEEB.OFFER_RECEIVED", "off_x")
            .Should().Be("jeeb://requests/off_x/offers");
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
