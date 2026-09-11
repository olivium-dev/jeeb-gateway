using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class RequestExpiryMathScheduledTests
{
    [Fact]
    public void Activated_Scheduled_Request_Gets_A_Full_Ttl_Window_From_Activation()
    {
        var createdAt = DateTimeOffset.Parse("2026-09-01T08:00:00Z");
        var activatedAt = createdAt + TimeSpan.FromDays(7);
        var request = new DeliveryRequest
        {
            Id = "scheduled-request",
            ClientId = "client-1",
            Status = RequestStatus.Pending,
            Description = "scheduled pickup",
            TierId = "urgent",
            CreatedAt = createdAt,
            ActivatedAt = activatedAt,
        };
        var flags = Substitute.For<IOptionsMonitor<UpstreamFeatureFlags>>();
        flags.CurrentValue.Returns(new UpstreamFeatureFlags());
        var windows = new TierExpiryWindowResolver(
            flags,
            Substitute.For<IDeliveryServiceClient>(),
            NullLogger<TierExpiryWindowResolver>.Instance);
        IReadOnlyDictionary<string, TimeSpan> ttls =
            new Dictionary<string, TimeSpan> { ["urgent"] = TimeSpan.FromMinutes(30) };

        var deadline = RequestExpiryMath.DeadlineFor(request, ttls, windows);

        deadline.Should().Be(activatedAt + TimeSpan.FromMinutes(30));
        RequestExpiryMath.IsExpiredAt(
                request, ttls, windows, activatedAt + TimeSpan.FromMinutes(1))
            .Should().BeFalse(
                "time spent scheduled must not consume the post-activation offer window");
    }

    [Fact]
    public void Immediate_Request_Still_Uses_CreatedAt()
    {
        var createdAt = DateTimeOffset.Parse("2026-09-01T08:00:00Z");
        var request = new DeliveryRequest
        {
            Id = "immediate-request",
            ClientId = "client-1",
            Status = RequestStatus.Pending,
            Description = "pickup now",
            TierId = "urgent",
            CreatedAt = createdAt,
        };
        var flags = Substitute.For<IOptionsMonitor<UpstreamFeatureFlags>>();
        flags.CurrentValue.Returns(new UpstreamFeatureFlags());
        var windows = new TierExpiryWindowResolver(
            flags,
            Substitute.For<IDeliveryServiceClient>(),
            NullLogger<TierExpiryWindowResolver>.Instance);
        IReadOnlyDictionary<string, TimeSpan> ttls =
            new Dictionary<string, TimeSpan> { ["urgent"] = TimeSpan.FromMinutes(30) };

        RequestExpiryMath.DeadlineFor(request, ttls, windows)
            .Should().Be(createdAt + TimeSpan.FromMinutes(30));
    }
}
