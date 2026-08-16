using System.Reflection;
using JeebGateway.Notifications;
using JeebGateway.Push;
using Xunit;

namespace JeebGateway.UnitTests;

// The stack was DELETED, not unregistered: unregistering leaves the classes available for
// someone to re-bind, and the stateless ratchet blacklists the source text, not the DI line.
public class InGatewayPushStackDeletedTests
{
    // NotificationTrigger survives the deletion, so it anchors the assembly and stops these
    // assertions passing vacuously if the namespace itself were renamed away.
    private static readonly Assembly Gateway = typeof(NotificationTrigger).Assembly;

    public static TheoryData<string> DeletedTypes() => new()
    {
        "JeebGateway.Push.IPushNotificationService",
        "JeebGateway.Push.PushNotificationService",
        "JeebGateway.Push.NotificationOwnerPushService",
        "JeebGateway.Push.IDeviceTokenStore",
        "JeebGateway.Push.InMemoryDeviceTokenStore",
        "JeebGateway.Push.IPushTransport",
        "JeebGateway.Push.InMemoryPushTransport",
        "JeebGateway.Push.DeviceToken",
        "JeebGateway.Push.DevicePlatform",
        "JeebGateway.Push.PushNotificationRequest",
        "JeebGateway.Push.PushDeliveryResult",
        "JeebGateway.Push.IPushDeliveryTracker",
        "JeebGateway.Push.InMemoryPushDeliveryTracker",
        "JeebGateway.Push.PushOptions",
        "JeebGateway.Push.PushTriggerCategoryMap",
    };

    [Theory]
    [MemberData(nameof(DeletedTypes))]
    public void The_in_gateway_push_stack_is_gone_from_the_assembly(string fullName)
    {
        // Control: the identical lookup still resolves a type that IS present.
        Assert.NotNull(Gateway.GetType("JeebGateway.Push.NotificationTrigger"));

        Assert.Null(Gateway.GetType(fullName));
    }

    [Fact]
    public void No_type_named_like_an_in_gateway_push_transport_or_token_store_survives()
    {
        var offenders = Gateway.GetTypes()
            .Where(t => t.Name is "IPushNotificationService" or "IPushTransport" or "IDeviceTokenStore"
                     || t.Name.Contains("PushTransport", StringComparison.Ordinal)
                     || t.Name.Contains("DeviceTokenStore", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();

        Assert.Empty(offenders);
    }

    // The replacement seam must exist, or the assertions above are satisfied by an
    // amputation rather than a migration.
    [Fact]
    public void The_hand_over_seam_that_replaced_it_is_present()
    {
        Assert.NotNull(Gateway.GetType("JeebGateway.Notifications.PushHandover"));
        Assert.NotNull(Gateway.GetType("JeebGateway.Notifications.IGenericEventDispatcher"));
        Assert.Equal(
            "jeeb.dispute_update", JeebGenericEventTypes.DisputeUpdateEventType);
    }
}
