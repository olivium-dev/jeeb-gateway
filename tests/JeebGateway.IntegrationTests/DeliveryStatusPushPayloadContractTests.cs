using System;
using System.Collections.Generic;
using FluentAssertions;
using JeebGateway.Notifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// The WIRE CONTRACT of the delivery-status push, pinned key by key.
///
/// <para><b>What this exists to stop.</b> Defect #3 of the three that made this category
/// inert: the payload carried no <c>type</c>/<c>category</c> discriminator and a
/// camelCase-only <c>deliveryId</c>. Receiver-side that is fatal twice over —
/// <c>NotificationCategory.fromData</c> resolves <c>other</c> and the handler drops the
/// message before any refresh signal, and the id-guarded refresh branch reads
/// <c>delivery_id | order_id | requestId | request_id</c>, a list camelCase
/// <c>deliveryId</c> is not in. A push that arrives and is dropped by the receiver looks
/// exactly like a push that was never sent, in every gateway-side instrument.</para>
///
/// <para><b>Why key-by-key and not a snapshot.</b> The failure mode is a payload TRIM — one
/// key quietly dropped by someone tidying duplicates ("why do we send both spellings?").
/// A snapshot test would go red on any change and get regenerated; these assertions each
/// name the receiver-side consequence of losing that one key, so the reason survives.</para>
///
/// <para>Host-free by construction: no WebApplicationFactory, no container, no push service.</para>
/// </summary>
public class DeliveryStatusPushPayloadContractTests
{
    private static Dictionary<string, object?> PayloadFor(
        string status = "InTransit", string previousStatus = "Picked")
        => DeliveryStatusPushNotifier.BuildPayload(new DeliveryStatusPushNotification(
            DeliveryId: "d-1",
            RequestId: "r-1",
            PreviousStatus: previousStatus,
            Status: status,
            Recipients: new[] { "client-1", "jeeber-1" },
            Title: "Delivery status updated",
            Body: "Status changed from Picked to InTransit.",
            GpsTrackingActive: true));

    [Fact]
    public void Carries_The_Type_Discriminator_The_Mobile_Handler_Routes_On()
    {
        PayloadFor()["type"].Should().Be("delivery",
            "without it NotificationCategory.fromData resolves `other` and the handler drops "
            + "the message before any refresh signal — the push arrives and drives nothing");
    }

    [Fact]
    public void Carries_The_Legacy_Category_Discriminator_Too()
    {
        PayloadFor()["category"].Should().Be("delivery",
            "older APKs read `category` and not `type`");
    }

    [Theory]
    [InlineData("delivery_id")]   // the alias the mobile id guard reads FIRST
    [InlineData("deliveryId")]    // kept for APKs that read the camelCase spelling
    [InlineData("requestId")]
    [InlineData("request_id")]
    public void Carries_Every_Id_Spelling_The_Receiver_Might_Read(string key)
    {
        PayloadFor().Should().ContainKey(key,
            "the mobile id alias list is delivery_id|order_id|requestId|request_id and the "
            + "id-guarded refresh branch returns early when none of them is present; sending "
            + "every spelling is what stops a future payload trim silently re-inerting this path");
        PayloadFor()[key].Should().NotBeNull();
    }

    [Fact]
    public void The_Payload_Is_FLAT_Strings_Only()
    {
        // The relay copies each top-level entry into the FCM `data` map and stringifies it.
        // A nested dictionary arrives client-side as ONE key holding stringified pseudo-JSON,
        // which no `data['...']` read can recover.
        foreach (var (key, value) in PayloadFor())
        {
            value.Should().BeOfType<string>(
                $"'{key}' must survive the relay's flat string->string data map");
        }
    }

    [Fact]
    public void Carries_No_Silent_Key()
    {
        // The relay treats `silent` as a transport switch: truthy sends data only, which posts
        // no shade entry and which iOS drops outright for a force-quit app. Owner ruling
        // 2026-07-27: delivery is shade + stored.
        PayloadFor().Should().NotContainKey("silent");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the whole file. If <c>BuildPayload</c> ever started returning an
    /// empty or defaulted map, every <c>ContainKey</c> above would fail — but a reader has no
    /// way to tell a meaningful pass from a vacuous one unless something asserts the values
    /// actually track the input.
    /// </summary>
    [Fact]
    public void The_Status_Fields_Track_The_Notification_And_Are_Not_Constants()
    {
        var picked = PayloadFor(status: "Picked", previousStatus: "Ordered");
        var atDoor = PayloadFor(status: "AtDoor", previousStatus: "InTransit");

        picked["status"].Should().Be("Picked");
        atDoor["status"].Should().Be("AtDoor");
        picked["previous_status"].Should().Be("Ordered");
        atDoor["previous_status"].Should().Be("InTransit");
        picked["status"].Should().NotBe(atDoor["status"]);
    }
}
