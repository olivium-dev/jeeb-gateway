using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Controllers;
using JeebGateway.JeebNotifications;
using JeebGateway.service.ServiceNotification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class JeebNotificationsDeepLinkResolutionTests
{
    [Fact]
    public async Task ListNotifications_IndexHit_ReplacesOfferIdWithRequestId()
    {
        var index = new RecordingOfferRequestIndex(
            offerId => offerId == "OFR-PROBE-3" ? "REQ-PROBE-3" : null);

        var page = await ListPage(index);

        page.Items[0].Ref.Should().Be("REQ-PROBE-3");
    }

    [Fact]
    public async Task ListNotifications_TopLevelRequestAliasWinsWithoutIndexLookup()
    {
        var index = new RecordingOfferRequestIndex(_ => "SHOULD-NOT-BE-USED");

        var page = await ListPage(
            index,
            Fm1NotificationWireFixtures.ConstructedOfferWithTopLevelRequestRef());

        page.Items.Should().ContainSingle().Which.Ref.Should().Be("REQ-TOP-LEVEL");
        index.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListNotifications_IndexNull_LeavesOfferRefNull()
    {
        var page = await ListPage(new RecordingOfferRequestIndex(_ => null));

        page.Items.Should().OnlyContain(item => item.Ref == null);
    }

    [Fact]
    public async Task ListNotifications_ThrowingIndex_StillReturnsPageWithNullRef()
    {
        var page = await ListPage(new RecordingOfferRequestIndex(
            _ => throw new InvalidOperationException("index unavailable")));

        page.Items.Should().HaveCount(3);
        page.Items.Should().OnlyContain(item => item.Ref == null);
    }

    [Fact]
    public async Task ListNotifications_BudgetExhausted_StopsRemainingLookupsAndLeavesRefsNull()
    {
        var index = new RecordingOfferRequestIndex(_ =>
        {
            Thread.Sleep(500);
            return "REQ-TOO-LATE";
        });

        var stopwatch = Stopwatch.StartNew();
        var page = await ListPage(index);
        stopwatch.Stop();

        index.CallCount.Should().Be(1);
        stopwatch.Elapsed.Should().BeLessThanOrEqualTo(
            JeebNotificationsInboxController.OfferResolutionBudget
            + TimeSpan.FromMilliseconds(100));
        page.Items.Should().OnlyContain(item => item.Ref == null);
    }

    [Fact]
    public async Task ListNotifications_DuplicateOfferIds_CostOneIndexLookup()
    {
        var index = new RecordingOfferRequestIndex(_ => "REQ-SHARED");

        var page = await ListPage(
            index,
            Fm1NotificationWireFixtures.OffersSharingOneOfferId());

        index.CallCount.Should().Be(1);
        page.Items.Should().OnlyContain(item => item.Ref == "REQ-SHARED");
    }

    private static async Task<JeebNotificationsPageResponse> ListPage(
        RecordingOfferRequestIndex index,
        object? wire = null)
    {
        var notifications = new FixtureNotificationClient(
            wire ?? Fm1NotificationWireFixtures.CapturedOfferReceived());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "FM1-PROBE-b02-20260726"),
                    new Claim("roles", "client"),
                ],
                authenticationType: "test")),
        };
        var controller = new JeebNotificationsInboxController(
            notifications,
            index,
            NullLogger<JeebNotificationsInboxController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.ListNotifications(userId: null);

        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<JeebNotificationsPageResponse>().Subject;
    }

    private sealed class FixtureNotificationClient : ServiceNotificationClient
    {
        private readonly object _wire;

        public FixtureNotificationClient(object wire)
            : base("http://127.0.0.1/", new HttpClient())
        {
            _wire = wire;
        }

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
            => Task.FromResult(_wire);
    }

    private sealed class RecordingOfferRequestIndex : IOfferRequestIndex
    {
        private readonly Func<string, string?> _resolve;

        public RecordingOfferRequestIndex(Func<string, string?> resolve)
        {
            _resolve = resolve;
        }

        public int CallCount { get; private set; }

        public void Record(string offerId, string requestId)
        {
        }

        public void Record(string offerId, string requestId, string? jeeberId)
        {
        }

        public string? ResolveRequestId(string offerId)
        {
            CallCount++;
            return _resolve(offerId);
        }

        public string? ResolveJeeberId(string offerId) => null;
    }
}
