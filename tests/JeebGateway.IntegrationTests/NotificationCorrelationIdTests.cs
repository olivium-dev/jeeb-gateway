using FluentAssertions;
using JeebGateway.Notifications;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class NotificationCorrelationIdTests
{
    [Fact]
    public void Create_IsDeterministic()
    {
        var first = NotificationCorrelationId.Create(
            "jeeb.offer_received",
            "client-1",
            "offer-1");

        var second = NotificationCorrelationId.Create(
            "jeeb.offer_received",
            "client-1",
            "offer-1");

        second.Should().Be(first);
    }

    [Fact]
    public void Create_UsesUuidVersion4ShapeAndRfc4122Variant()
    {
        var value = NotificationCorrelationId.Create(
            "jeeb.offer_received",
            "client-1",
            "offer-1");

        value[14].Should().Be('4');
        value[19].Should().BeOneOf('8', '9', 'a', 'b');
        Guid.TryParseExact(value, "D", out _).Should().BeTrue();
    }

    [Fact]
    public void Create_IsDistinctAcrossEveryInput()
    {
        var baseline = NotificationCorrelationId.Create(
            "jeeb.offer_received",
            "client-1",
            "offer-1");

        var values = new[]
        {
            baseline,
            NotificationCorrelationId.Create("jeeb.offer_accepted", "client-1", "offer-1"),
            NotificationCorrelationId.Create("jeeb.offer_received", "client-2", "offer-1"),
            NotificationCorrelationId.Create("jeeb.offer_received", "client-1", "offer-2"),
        };

        values.Should().OnlyHaveUniqueItems();
    }
}
