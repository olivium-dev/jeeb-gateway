using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.JeebNotifications;
using JeebGateway.Notifications;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class P02NotificationContractFailureTests
{
    public static IEnumerable<object[]> MalformedTargets()
    {
        foreach (var kind in new[] { "chat", "new_request", "delivery", "offer", "offer_accepted", "jeeb.support.updated" })
        foreach (var id in new[] { "/", "one/two", "x?query", "x#fragment", "x\\y", "%2F", "%252F", ".", "..", "white space", "x\ny" })
            yield return new object[] { kind, id };
    }

    [Theory]
    [MemberData(nameof(MalformedTargets))]
    public void Resolver_Rejects_A_Present_Invalid_Destination(string kind, string id)
    {
        var act = () => NotificationDeepLinkResolver.Resolve(kind, id);
        act.Should().Throw<NotificationContractException>();
    }

    [Theory]
    [InlineData("chat", "requestId")]
    [InlineData("new_request", "requestId")]
    [InlineData("delivery", "deliveryId")]
    [InlineData("offer_accepted", "requestId")]
    [InlineData("chat", "ref")]
    public void Malformed_Id_Fails_Before_Ref_Projection(string kind, string field)
    {
        var row = new JObject { ["id"] = "notification", ["type"] = kind, [field] = "/" };
        var act = () => JeebNotificationsInboxController.ExtractRowsForTests(new JArray(row));
        act.Should().Throw<NotificationContractException>();
    }

    [Theory]
    [InlineData("42")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Destination_Is_A_String_Not_A_Coerced_Scalar(string json)
    {
        var row = new JObject { ["type"] = "chat", ["requestId"] = JToken.Parse(json) };
        var act = () => JeebNotificationsInboxController.ExtractRowsForTests(new JArray(row));
        act.Should().Throw<NotificationContractException>();
    }

    [Theory]
    [InlineData("https://example.invalid/chat/req")]
    [InlineData("jeeb://chat//")]
    [InlineData("jeeb://chat/%2f")]
    [InlineData("jeeb://orders/../chat/req")]
    [InlineData("jeeb://chat/..")]
    [InlineData("jeeb://unsupported/req")]
    [InlineData("/chat/../req?valid=query")]
    [InlineData("/chat/%2f?valid=query")]
    [InlineData("/chat/req//")]
    [InlineData("//chat/req")]
    [InlineData("jeeb:///chat/req")]
    [InlineData("jeeb://chat/req?bad=%ZZ")]
    [InlineData("")]
    public void Malformed_Explicit_Link_Cannot_Win_Over_A_Valid_Ref(string link)
    {
        var row = new JObject { ["type"] = "chat", ["requestId"] = "valid-request",
            ["metadata"] = new JObject { ["deep_link"] = link } };
        var extract = () => JeebNotificationsInboxController.ExtractRowsForTests(new JArray(row));
        extract.Should().Throw<NotificationContractException>();
        var project = () => JeebNotificationsProjection.ProjectItem(new UpstreamNotificationRow
            { Type = "chat", Ref = "valid-request", DeepLink = link });
        project.Should().Throw<NotificationContractException>();
    }

    [Theory]
    [InlineData("jeeb://chat/req-1")]
    [InlineData("jeeb://support/tickets/case-42")]
    [InlineData("jeeb://orders/req-1/receipt")]
    [InlineData("jeeb://wallet")]
    [InlineData("jeeb://chat/req-1?source=inbox")]
    [InlineData("jeeb://orders/req-1/receipt/?source=inbox&label=order%20details")]
    [InlineData("jeeb://wallet/")]
    [InlineData("jeeb://chat/req-1#message")]
    [InlineData("/chat/req-1")]
    [InlineData("/chat/req-1/?source=inbox")]
    [InlineData("/settings/notifications?section=delivery")]
    [InlineData("/")]
    [InlineData("/?source=inbox")]
    [InlineData("JEEB://CHAT/req-1")]
    public void Valid_Explicit_Link_Is_Preserved(string link)
    {
        JeebNotificationsProjection.ProjectItem(new UpstreamNotificationRow
            { Type = "chat", Ref = "req-1", DeepLink = link }).DeepLink.Should().Be(link);
        var row = new JObject { ["type"] = "chat", ["requestId"] = "req-1",
            ["payload"] = new JObject { ["deepLink"] = link } };
        var extracted = JeebNotificationsInboxController.ExtractRowsForTests(new JArray(row)).Rows.Single();
        JeebNotificationsProjection.ProjectItem(extracted).DeepLink.Should().Be(link);
    }

    [Theory]
    [InlineData("request-id")]
    [InlineData("req_123")]
    [InlineData("defb1f07-efa5-4b8f-bc1a-09d6fcd1140b")]
    public void Opaque_NonUuid_Destinations_Remain_Valid(string id)
    {
        NotificationDeepLinkResolver.Resolve("chat", id).Should().Be("jeeb://chat/" + id);
    }

    [Fact]
    public void Explicit_Link_Outer_Whitespace_Is_Normalized_Like_Mobile()
    {
        NotificationDeepLinkResolver.ValidateExplicitLink("  jeeb://chat/req-1?source=inbox  ")
            .Should().Be("jeeb://chat/req-1?source=inbox");
    }

    [Fact]
    public void Absent_Destination_Is_Distinct_From_Malformed_Destination()
    {
        var rows = JeebNotificationsInboxController.ExtractRowsForTests(
            new JArray(new JObject { ["type"] = "chat" })).Rows;
        rows.Single().Ref.Should().BeNull();
        JeebNotificationsProjection.ProjectItem(rows.Single()).DeepLink.Should().Be(NotificationDeepLinkResolver.InboxRoot);
    }
}
