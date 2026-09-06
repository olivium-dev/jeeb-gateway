using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.JeebNotifications;
using JeebGateway.Notifications;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class P02NotificationTargetContractTests
{
    [Theory]
    [InlineData("karim", 31)]
    [InlineData("nour", 28)]
    public void Sanitized_Captured_Page_Resolves_Every_Addressed_Row(string actor, int count)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FM1", $"captured-msi-{actor}-inbox-20260905-page.json");
        var json = File.ReadAllText(path);
        foreach (var forbidden in new[] { "_dispatch", "_idempotency_fingerprint", "senderProfilePicture", "nickname", "media_links", "Authorization", "Bearer " })
            json.Should().NotContain(forbidden);
        var (rows, total) = JeebNotificationsInboxController.ExtractRowsForTests(JObject.Parse(json));
        rows.Should().HaveCount(count);
        total.Should().Be(count);
        var missing = rows.Where(row => row.Type != "availability" && string.IsNullOrWhiteSpace(row.Ref)).ToArray();
        if (actor == "karim")
        {
            missing.Select(row => row.Id).Should().BeEquivalentTo(
                "4572e4e3-4c3f-4e41-9c0b-ae7961410437", "3b86aec9-e1cc-44fa-aba2-21b952c67e04",
                "34efe703-6222-4bcd-be42-a6daa22b3e88", "e4363780-bc13-4cc5-a387-1d10d187e089");
            missing.Should().OnlyContain(row => row.Type == "offer_accepted");
            missing.Select(JeebNotificationsProjection.ProjectItem).Should()
                .OnlyContain(item => item.DeepLink == NotificationDeepLinkResolver.InboxRoot);
        }
        else missing.Should().BeEmpty();
        rows.Should().OnlyContain(row => !string.IsNullOrWhiteSpace(row.Timestamp));
        foreach (var row in rows)
            AssertMobileGrammar(JeebNotificationsProjection.ProjectItem(row).DeepLink);
        var expected = new Dictionary<string, string>
        {
            ["f5cab53e"] = "defb1f07-efa5-4b8f-bc1a-09d6fcd1140b",
            ["4b241ba5"] = "13b8bca2-ee0f-4acf-9db3-c364d5984a03",
            ["cab0d955"] = "fd91232f-8482-4a4e-ba3f-b356b24ab71b",
            ["788fbef4"] = "fd91232f-8482-4a4e-ba3f-b356b24ab71b",
            ["4061357f"] = "96e5c26b-b4cf-4e53-8744-2c2f0affc4b1"
        };
        var applicable = actor == "karim" ? new[] { "f5cab53e", "4b241ba5", "cab0d955" }
            : new[] { "788fbef4", "4061357f" };
        foreach (var prefix in applicable)
            rows.Single(row => row.Id?.StartsWith(prefix, StringComparison.Ordinal) == true).Ref.Should().Be(expected[prefix]);
    }

    [Theory]
    [InlineData("new_request")]
    [InlineData("chat")]
    [InlineData("chat_message")]
    [InlineData("request.try_expand_tier")]
    [InlineData("request.expired")]
    [InlineData("offer_lost")]
    public void Request_Routes_Use_Top_Request_Before_Payload_And_Never_Conversation(string type)
    {
        var obj = new JObject { ["type"] = type, ["requestId"] = "top-request",
            ["payload"] = new JObject { ["request_id"] = "payload-request", ["conversationId"] = "conversation", ["offer_id"] = "offer" } };
        Extract(obj).Ref.Should().Be("top-request");
        obj.Remove("requestId");
        Extract(obj).Ref.Should().Be("payload-request");
        ((JObject)obj["payload"]!).Remove("request_id");
        Extract(obj).Ref.Should().BeNull();
    }

    [Theory]
    [InlineData("offer")]
    [InlineData("offer_accepted")]
    [InlineData("jeeb.offer_accepted")]
    public void Typed_Offer_Payload_Request_Wins_Over_Top_Request(string type)
    {
        var obj = new JObject { ["type"] = type, ["requestId"] = "top-request",
            ["payload"] = new JObject { ["request_id"] = "payload-request", ["offer_id"] = "offer" } };
        Extract(obj).Ref.Should().Be("payload-request");
    }

    [Theory]
    [InlineData("delivery")]
    [InlineData("delivery_status_updated")]
    [InlineData("cancellation_decision")]
    public void Delivery_Id_Precedes_Request_And_Payload_Aliases(string type)
    {
        var obj = new JObject { ["type"] = type, ["deliveryId"] = "top-delivery", ["requestId"] = "top-request",
            ["payload"] = new JObject { ["delivery_id"] = "payload-delivery", ["requestId"] = "payload-request" } };
        Extract(obj).Ref.Should().Be("top-delivery");
        obj.Remove("deliveryId");
        Extract(obj).Ref.Should().Be("payload-delivery");
    }

    [Theory]
    [InlineData("chat", "requestId")]
    [InlineData("new_request", "requestId")]
    [InlineData("offer_accepted", "requestId")]
    [InlineData("delivery", "deliveryId")]
    public void Typed_Destination_Precedes_Generic_Legacy_Alias(string type, string field)
    {
        var obj = new JObject { ["type"] = type, [field] = "actual-destination", ["ref"] = "legacy-alias" };
        Extract(obj).Ref.Should().Be("actual-destination");
    }

    [Theory]
    [InlineData("6a9bfb560000000000000000", true)]
    [InlineData("000000000000000000000000", true)]
    [InlineData("ffffffffffffffffffffffff", true)]
    [InlineData("6a9bfb56ZZZZZZZZZZZZZZZZ", false)]
    [InlineData("6a9bfb56", false)]
    [InlineData(null, false)]
    public void ObjectId_Timestamp_Validates_Whole_Value(string? id, bool valid)
    {
        var obj = new JObject { ["type"] = "chat", ["_id"] = id };
        var actual = Extract(obj).Timestamp;
        if (!valid) { actual.Should().BeNull(); return; }
        DateTimeOffset.Parse(actual!).ToUnixTimeSeconds().Should().Be(Convert.ToUInt32(id![..8], 16));
        obj["at"] = "2026-09-01T00:00:00Z";
        Extract(obj).Timestamp.Should().Be("2026-09-01T00:00:00Z");
        obj["ts"] = "2026-09-02T00:00:00Z";
        Extract(obj).Timestamp.Should().Be("2026-09-02T00:00:00Z");
    }

    [Theory]
    [InlineData("new_request")]
    [InlineData("chat")]
    [InlineData("offer")]
    [InlineData("offer_accepted")]
    [InlineData("delivery")]
    [InlineData("request.expired")]
    [InlineData("kyc_approved")]
    [InlineData("settlement_paid")]
    [InlineData("jeeb.dispute.updated")]
    [InlineData("jeeb.support.updated")]
    public void Emitted_Links_Match_Mobile_Allow_List(string type)
    {
        AssertMobileGrammar(NotificationDeepLinkResolver.Resolve(type, "request-id"));
        AssertMobileGrammar(NotificationDeepLinkResolver.Resolve(type, null));
    }

    private static UpstreamNotificationRow Extract(JObject obj) =>
        JeebNotificationsInboxController.ExtractRowsForTests(new JArray(obj)).Rows.Single();

    // Pinned to mobile notification_deep_link.dart at c5603c38; host is the first segment.
    private static void AssertMobileGrammar(string link)
    {
        var patterns = new[]
        {
            "^/$", "^/notifications$", "^/wallet(/(customer|activity|charge-info))?$", "^/earnings$",
            "^/profile/kyc$", "^/settings/notifications$", "^/support$", "^/jeeber/pending-offers$",
            "^/chat/[^/]+$", "^/disputes/[^/]+$", "^/support/tickets/[^/]+$", "^/wallet/transactions/[^/]+$",
            "^/requests/[^/]+/(offers|waiting)$", "^/orders/[^/]+(/(receipt|summary|cancel|rate|tracking|otp|feedback|mutual-rate|escalate))?$",
            "^/jeeber/requests/[^/]+(/offer)?$", "^/jeeber/deliveries/[^/]+/active$"
        };
        var uri = new Uri(link);
        uri.Scheme.Should().Be("jeeb");
        patterns.Any(pattern => Regex.IsMatch("/" + uri.Host + uri.AbsolutePath.TrimEnd('/'), pattern)).Should().BeTrue(link);
    }
}
