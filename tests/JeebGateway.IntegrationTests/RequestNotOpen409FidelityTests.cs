using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// sprint-009 Lane E — 409 fidelity on submit. offer-service reuses HTTP 409 for three
/// distinct conflicts; the store must map each to the RIGHT signal. Before this fix, an
/// upstream <c>request_not_open</c> (auction closed) was swept into the retired offer-cap
/// message. Now it maps to <see cref="RequestNotOpenForOffersException"/> →
/// <c>request-not-open-for-offers</c> ProblemDetails, while duplicate and generic codes
/// keep their existing mapping (regression guard).
/// </summary>
public class RequestNotOpen409FidelityTests
{
    // -----------------------------------------------------------------
    // Store-level unit tests (the mechanism)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_UpstreamRequestNotOpen_Throws_RequestNotOpen_NotCap()
    {
        var store = new UpstreamPendingOffersStore(new ConflictClient("request_not_open"));

        Func<Task> act = () => store.TrySubmitAsync(
            "req-1", "jeeber-1", 5m, 10, null, 20, DateTimeOffset.UtcNow, CancellationToken.None);

        await act.Should().ThrowAsync<RequestNotOpenForOffersException>(
            "a closed auction must not render the 20-offer-cap exception");
        // Explicitly assert it is NOT the cap exception (the old behaviour).
        await act.Should().NotThrowAsync<TooManyOffersForRequestException>();
    }

    [Fact]
    public async Task Submit_UpstreamDuplicate_Still_Throws_Duplicate()
    {
        var store = new UpstreamPendingOffersStore(new ConflictClient("offer_already_exists"));

        Func<Task> act = () => store.TrySubmitAsync(
            "req-1", "jeeber-1", 5m, 10, null, 20, DateTimeOffset.UtcNow, CancellationToken.None);

        await act.Should().ThrowAsync<DuplicateOfferException>();
    }

    [Fact]
    public async Task Submit_UpstreamUnknownConflictCode_Maps_To_GenericConflict_NotCap()
    {
        var store = new UpstreamPendingOffersStore(new ConflictClient("some_other_cap_code"));

        Func<Task> act = () => store.TrySubmitAsync(
            "req-1", "jeeber-1", 5m, 10, null, int.MaxValue, DateTimeOffset.UtcNow, CancellationToken.None);

        await act.Should().ThrowAsync<OfferSubmitConflictException>();
        await act.Should().NotThrowAsync<TooManyOffersForRequestException>();
    }

    // -----------------------------------------------------------------
    // Controller E2E — the rendered ProblemDetails
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_WhenUpstreamRequestNotOpen_Renders_RequestNotOpen_ProblemDetails_NotCap()
    {
        var fake = new ConflictClient("request_not_open");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(fake);
                });
            });

        // The LOCAL request row is pending (pre-acceptance), so the controller's local
        // gate passes and the submit reaches the upstream store, which returns the
        // request_not_open 409 (the local read-model lagged the closed auction).
        var clientId = $"client-{Guid.NewGuid()}";
        string requestId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
            var created = await store.CreateAsync(new CreateRequestInput
            {
                ClientId = clientId,
                Description = "closed upstream, pending locally",
            }, default);
            requestId = created.Id;
        }

        var jeeber = factory.CreateClient();
        jeeber.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        jeeber.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/request-not-open-for-offers",
            "an upstream request_not_open must render its own reason, not the 20-offer cap");
        problem.Type.Should().NotBe("https://jeeb.dev/errors/offers-per-request-exceeded");
    }

    [Fact]
    public async Task Gateway_Imposes_No_Offer_Count_Cap_Across_Requests()
    {
        var fake = new AcceptingSubmitClient();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(fake);
                });
            });

        var jeeberId = $"jeeber-many-{Guid.NewGuid()}";
        var jeeber = factory.CreateClient();
        jeeber.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        jeeber.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var requestIds = new List<string>();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
            for (var i = 0; i < 25; i++)
            {
                var created = await store.CreateAsync(new CreateRequestInput
                {
                    ClientId = $"client-many-{i}-{Guid.NewGuid()}",
                    Description = $"open request {i}",
                }, default);
                requestIds.Add(created.Id);
            }
        }

        foreach (var requestId in requestIds)
        {
            var resp = await jeeber.PostAsJsonAsync(
                $"/requests/{requestId}/offers",
                new { fee = 9m, etaMinutes = 20 });

            resp.StatusCode.Should().Be(HttpStatusCode.Created,
                "one jeeber may submit to more than the retired 20-request threshold when each request is distinct");
            resp.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        }

        fake.Submits.Should().HaveCount(requestIds.Count);
        fake.Submits.Should().OnlyContain(s => s.ActingUserId == jeeberId);
        fake.Submits.Select(s => s.RequestId).Should().BeEquivalentTo(requestIds);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Minimal offer-service client whose <see cref="SubmitAsync"/> always throws a
    /// 409 <see cref="OfferUpstreamConflictException"/> with a configured error code.
    /// </summary>
    private sealed class ConflictClient : IOfferServiceClient
    {
        private readonly string _code;
        public ConflictClient(string code) => _code = code;

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new OfferUpstreamConflictException(requestId, _code);

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Minimal offer-service client whose <see cref="SubmitAsync"/> accepts every
    /// request-scoped offer with a 201-equivalent wire record.
    /// </summary>
    private sealed class AcceptingSubmitClient : IOfferServiceClient
    {
        public List<SubmitCall> Submits { get; } = new();

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
        {
            Submits.Add(new SubmitCall(actingUserId, requestId));
            return Task.FromResult(new OfferWire
            {
                Id = $"offer-{Submits.Count}",
                RequestId = requestId,
                JeeberId = actingUserId,
                FeeCents = feeCents,
                EtaMinutes = etaMinutes,
                Note = note,
                Status = "submitted",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed record SubmitCall(string ActingUserId, string RequestId);
}
