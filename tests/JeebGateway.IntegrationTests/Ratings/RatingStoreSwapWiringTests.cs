using FluentAssertions;
using JeebGateway.Ratings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Ratings;

/// <summary>
/// Gap 8 — DI-wiring guard for the flag-gated rating store swap in
/// <c>Program.cs</c> (Ban store-swap precedent). When
/// <c>FeatureFlags:UseUpstream:Ratings</c> is true the singleton
/// <see cref="IRatingStore"/> resolves to <see cref="FeedbackServiceRatingStore"/>
/// (feedback-service-backed durable record-of-truth); when false it stays
/// <see cref="InMemoryRatingStore"/> (the instant-rollback fallback, KEPT). The
/// blind/reveal state machine (<see cref="BlindRevealPolicy"/>) is unchanged in
/// both cases; only the backing store swaps.
/// </summary>
public class RatingStoreSwapWiringTests
{
    [Fact]
    public void Flag_Off_Resolves_InMemoryRatingStore()
    {
        // Default factory — Ratings flag is off in appsettings.json.
        using var factory = new WebApplicationFactory<Program>();

        var store = factory.Services.GetRequiredService<IRatingStore>();

        store.Should().BeOfType<InMemoryRatingStore>(
            "the legacy in-memory store is the flag-off record-of-truth and rollback target");
    }

    [Fact]
    public void Flag_On_Resolves_FeedbackServiceRatingStore()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Ratings", "true");
            // Flipping the flag on makes feedback-service the rating record-of-truth, so
            // Program.cs requires a reachable FeedbackServiceApi:BaseUrl (not the dead dev
            // placeholder) or it fails fast at startup. This wiring test only asserts the DI
            // store-type swap, so any non-placeholder URL satisfies the startup guard.
            builder.UseSetting("FeedbackServiceApi:BaseUrl", "http://feedback-service.test");
        });

        var store = factory.Services.GetRequiredService<IRatingStore>();

        store.Should().BeOfType<FeedbackServiceRatingStore>(
            "flag-on repoints the delivery-ratings record-of-truth to feedback-service");
    }
}
