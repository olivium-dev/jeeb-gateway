using System.Net.Http;
using System.Security.Claims;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Controllers;
using JeebGateway.service.ServiceNotification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S1 / D-FM1-04 — the self-scoped mark-read action must prove ownership through
/// the receiver-scoped notification-service read before issuing the upstream PATCH.
/// </summary>
public sealed class JeebNotificationsMarkReadOwnershipTests
{
    [Fact]
    public async Task MarkRead_ForeignNotification_Returns404_AndDoesNotPatch()
    {
        var upstream = new RecordingNotificationClient();
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "caller-a"),
                new Claim("roles", "client"),
            ],
            authenticationType: "test"));
        var controller = new JeebNotificationsInboxController(
            upstream,
            new MissingOfferRequestIndex(),
            NullLogger<JeebNotificationsInboxController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var response = await controller.MarkRead("owned-by-b");

        response.Should().BeOfType<NotFoundResult>();
        upstream.ReceiverReads.Should().Equal("caller-a");
        upstream.PatchCount.Should().Be(0);
    }

    private sealed class MissingOfferRequestIndex : IOfferRequestIndex
    {
        public void Record(string offerId, string requestId)
        {
        }

        public void Record(string offerId, string requestId, string? jeeberId)
        {
        }

        public string? ResolveRequestId(string offerId) => null;

        public string? ResolveJeeberId(string offerId) => null;
    }

    private sealed class RecordingNotificationClient : ServiceNotificationClient
    {
        private const string ReceiverScopedWireJson =
            """
            {
              "messages": [
                {
                  "notification_id": "owned-by-a",
                  "notification_type": "jeeb.offer_received",
                  "status": "delivered"
                }
              ],
              "total_messages": 1
            }
            """;

        public RecordingNotificationClient()
            : base("http://127.0.0.1/", new HttpClient())
        {
        }

        public List<string> ReceiverReads { get; } = [];
        public int PatchCount { get; private set; }

        public override Task<object> Get_messages_by_receiver_messages_receiver__receiver_id__getAsync(
            string receiver_id,
            int? page,
            int? page_size,
            string read_status,
            Notification_type2 notification_type,
            Anonymous sender,
            Created_after2 created_after,
            Created_before2 created_before,
            CancellationToken cancellationToken)
        {
            ReceiverReads.Add(receiver_id);
            return Task.FromResult<object>(JObject.Parse(ReceiverScopedWireJson));
        }

        public override Task<object> Mark_notification_read_notifications__notification_id__mark_read_patchAsync(
            string notification_id,
            CancellationToken cancellationToken)
        {
            PatchCount++;
            return Task.FromResult<object>(new JObject());
        }
    }
}
