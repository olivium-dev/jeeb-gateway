using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class ScheduledRequestOfferWindowTests
{
    [Fact]
    public async Task Activation_Does_Not_Immediately_Nudge_An_Old_Scheduled_Request()
    {
        using var factory = new Fakes.FakeOfferStoreWebApplicationFactory();
        var clock = factory.Services
            .GetRequiredService<JeebGateway.TestControlPlane.FakeTimeProvider>();
        var createdAt = clock.GetUtcNow();
        var scheduledAt = createdAt + TimeSpan.FromDays(7);
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var request = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = "scheduled-nudge-client",
            Description = "scheduled request",
            TierId = "urgent",
            ScheduledAt = scheduledAt,
        }, CancellationToken.None);

        // Open matching at the default 30-minute buffer. CreatedAt is now nearly seven
        // days old, but ActivatedAt is the beginning of this request's offer window.
        clock.AdvanceBy(scheduledAt - TimeSpan.FromMinutes(30) - createdAt);
        await factory.Services.GetRequiredService<ScheduledDeliveryActivator>()
            .SweepOnceAsync(CancellationToken.None);
        var activated = await store.GetAsync(request.Id, CancellationToken.None);
        activated!.Status.Should().Be(RequestStatus.Pending);
        activated.ActivatedAt.Should().BeCloseTo(clock.GetUtcNow(), TimeSpan.FromSeconds(1));

        var sweeper = factory.Services.GetRequiredService<RequestNudgeSweeper>();
        await sweeper.SweepOnceAsync(CancellationToken.None);

        var notifier = factory.Services.GetRequiredService<InMemoryRequestExpiryNotifier>();
        notifier.Nudges.Should().NotContain(nudge => nudge.RequestId == request.Id,
            "activation must start a fresh no-offer window");

        clock.AdvanceBy(TimeSpan.FromMinutes(11));
        await sweeper.SweepOnceAsync(CancellationToken.None);

        notifier.Nudges.Should().ContainSingle(nudge => nudge.RequestId == request.Id);
    }
}
