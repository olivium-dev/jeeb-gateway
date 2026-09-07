using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

// No host, HTTP client, credentials, stores or dispatchers are created by these tests.
public sealed class ServiceCallbackRouteAliasTests
{
    [Theory]
    [InlineData("jeeb.offer_updated", "requestId", "entityId", "jeeb://requests/req7/offers")]
    [InlineData("jeeb.offer_updated", "request_id", "entity_id", "jeeb://requests/req7/offers")]
    [InlineData("jeeb.offer_updated", "requestId", "deliveryId", "jeeb://requests/req7/offers")]
    [InlineData("jeeb.offer_accepted", "requestId", "entityId", "jeeb://chat/req7")]
    [InlineData("jeeb.delivery_status_updated", "deliveryId", "entityId", "jeeb://orders/req7")]
    [InlineData("jeeb.delivery_status_updated", "delivery_id", "requestId", "jeeb://orders/req7")]
    public void Typed_Target_Wins_Over_Conflicting_Alias(string type, string typedKey, string alias, string expected)
    {
        Payload(type, new() { [typedKey] = "req7", [alias] = "offer42" })["deepLink"]
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("jeeb.offer_updated", "entityId", "jeeb://requests/legacy7/offers")]
    [InlineData("jeeb.offer_updated", "entity_id", "jeeb://requests/legacy7/offers")]
    [InlineData("jeeb.offer_updated", "id", "jeeb://requests/legacy7/offers")]
    [InlineData("jeeb.delivery_status_updated", "delivery_id", "jeeb://orders/legacy7")]
    [InlineData("jeeb.delivery_status_updated", "entityId", "jeeb://orders/legacy7")]
    [InlineData("offer_lost", "requestId", "jeeb://notifications")]
    [InlineData("jeeb.offer_lost", "request_id", "jeeb://notifications")]
    [InlineData("jeeb.offer_updated", "offerId", "jeeb://notifications")]
    [InlineData("jeeb.offer_updated", "deliveryId", "jeeb://notifications")]
    public void Legacy_And_Nonrouting_Aliases_Keep_Their_Resource_Semantics(string type, string key, string expected)
    {
        Payload(type, new() { [key] = "legacy7" })["deepLink"].Should().Be(expected);
    }

    [Theory]
    [InlineData("requestId", "req/7")]
    [InlineData("request_id", "../admin")]
    [InlineData("requestId", "req?7")]
    [InlineData("requestId", "req%2F7")]
    [InlineData("requestId", " req7")]
    [InlineData("requestId", "")]
    [InlineData("request_id", " ")]
    [InlineData("request_id", "different-request")]
    public async Task Malformed_Typed_Target_Cannot_Hide_Behind_Valid_Aliases(string typedKey, string value)
    {
        // Null downstream collaborators make any reservation/dispatch an immediate failure.
        var controller = new ServiceCallbacksController(null!, null!, null!, null!,
            NullLogger<ServiceCallbacksController>.Instance);
        var result = await controller.Notify(new ServiceCallbackNotifyRequest
        {
            NotificationType = "jeeb.offer_updated", RecipientUserId = "recipient7",
            IdempotencyKey = "alias-regression",
            Data = new Dictionary<string, string>
            {
                ["entityId"] = "offer42", ["deliveryId"] = "delivery9",
                ["requestId"] = "req7", [typedKey] = value,
            },
        }, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static Dictionary<string, object?> Payload(string type, Dictionary<string, string> data)
    {
        var method = typeof(ServiceCallbacksController).GetMethod("BuildPayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Dictionary<string, object?>)method.Invoke(null, new object?[]
        {
            type, new NotificationTemplate("title", "body"),
            new ServiceCallbackNotifyRequest { Data = data }, null,
        })!;
    }
}
