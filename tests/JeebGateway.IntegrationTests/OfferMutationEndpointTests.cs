using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
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
/// S08 A3 / A5 — the gateway's offer MUTATION BFF (<c>PUT /v1/offers/{id}</c> edit,
/// <c>POST /v1/offers/{id}/reject</c>). When <c>FeatureFlags:UseUpstream:Offer =
/// true</c> the gateway forwards to offer-service via
/// <see cref="IOfferServiceClient.EditAsync"/> /
/// <see cref="IOfferServiceClient.RejectAsync"/> and re-emits the upstream status
/// VERBATIM (200 / 403 / 404 / 409) — it re-derives no auction rule.
///
/// offer-service is replaced by a <see cref="FakeOfferMutationClient"/> so the
/// suite asserts the gateway's status mapping + identity/requestId forwarding
/// deterministically. The offerId → requestId pairing (needed only for the
/// request-scoped edit route) is seeded via the real <see cref="IOfferRequestIndex"/>,
/// exactly what RequestOffersController.Submit records on a real submit.
///
/// Capability gates: A3 edit is keyed {jeeber} (offer.edit.own); A5 reject is keyed
/// {client} (offer.reject). The wrong-role caller is 403'd at the L2 capability
/// gate before the controller runs (asserted below).
/// </summary>
public class OfferMutationEndpointTests
{
    // ---------------------------------------------------------------------
    // A3 — edit (happy + negatives)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A3_Edit_Forwards_To_Upstream_And_Returns_200_With_Projection()
    {
        var fake = new FakeOfferMutationClient
        {
            EditResult = new OfferMutationResult
            {
                Status = OfferMutationStatus.Ok,
                Offer = new OfferWire
                {
                    Id = "offer-a3",
                    RequestId = "req-a3",
                    JeeberId = "kamal-jeeber",
                    FeeCents = 3300, // $33 — the A3 edit
                    EtaMinutes = 25,
                    Note = "On my way",
                    Status = "edited",
                    EditsCount = 1,
                },
            },
        };
        using var factory = NewFactory(fake);
        SeedRouting(factory, "offer-a3", "req-a3", "kamal-jeeber");

        var resp = await JeeberActor(factory, "kamal-jeeber").SendAsync(
            Put("/v1/offers/offer-a3", new { fee = 33 }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OfferWire>();
        body!.Id.Should().Be("offer-a3");
        body.Status.Should().Be("edited");
        // The gateway resolved the request-scoped route + forwarded the actor + the
        // edit converted dollars→cents ($33 -> 3300). It re-derived no edit rule.
        fake.LastEditRequestId.Should().Be("req-a3");
        fake.LastEditOfferId.Should().Be("offer-a3");
        fake.LastEditActor.Should().Be("kamal-jeeber");
        fake.LastEditFeeCents.Should().Be(3300);
        // A partial edit (fee only) left eta/note unsent (null).
        fake.LastEditEtaMinutes.Should().BeNull();
        fake.LastEditNote.Should().BeNull();
        // JEB-1474: the gateway supplies the Jeeb edit cap (2) — the shared
        // service no longer hardcodes it.
        fake.LastEditMaxEdits.Should().Be(2);
    }

    [Fact]
    public async Task A3_Edit_UnknownOffer_Returns_404_Without_Calling_Upstream()
    {
        var fake = new FakeOfferMutationClient
        {
            EditResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake);
        // No SeedRouting → the request-scoped route cannot be resolved.

        var resp = await JeeberActor(factory, "kamal-jeeber").SendAsync(
            Put($"/v1/offers/{Guid.NewGuid()}", new { fee = 33 }));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.EditCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A3_Edit_EmptyBody_Returns_400()
    {
        var fake = new FakeOfferMutationClient
        {
            EditResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake);
        SeedRouting(factory, "offer-empty", "req-empty", "kamal-jeeber");

        var resp = await JeeberActor(factory, "kamal-jeeber").SendAsync(
            Put("/v1/offers/offer-empty", new { }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.EditCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A3_Edit_UpstreamConflict_Returns_409_VerbatimNot500()
    {
        // offer-service rejects (edit cap reached / not pending). The gateway forwards
        // the 409 — it must NOT mask it as a 500.
        var fake = new FakeOfferMutationClient
        {
            EditResult = new OfferMutationResult { Status = OfferMutationStatus.Conflict },
        };
        using var factory = NewFactory(fake);
        SeedRouting(factory, "offer-409", "req-409", "kamal-jeeber");

        var resp = await JeeberActor(factory, "kamal-jeeber").SendAsync(
            Put("/v1/offers/offer-409", new { fee = 40 }));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-pending");
    }

    [Fact]
    public async Task A3_Edit_AsClient_Is403_AtCapabilityGate_NeverReachesUpstream()
    {
        // Editing is a JEEBER action (offer.edit.own keyed {jeeber}). A client caller
        // is rejected at the L2 capability gate; the upstream is NEVER called.
        var fake = new FakeOfferMutationClient
        {
            EditResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake);
        SeedRouting(factory, "offer-clientedit", "req-clientedit", "kamal-jeeber");

        var resp = await ClientActor(factory, "sami-client").SendAsync(
            Put("/v1/offers/offer-clientedit", new { fee = 33 }));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fake.EditCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A3_Edit_FlagOff_Returns_503()
    {
        var fake = new FakeOfferMutationClient
        {
            EditResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake, offerUpstream: false); // kill-switch off
        SeedRouting(factory, "offer-flagoff", "req-flagoff", "kamal-jeeber");

        var resp = await JeeberActor(factory, "kamal-jeeber").SendAsync(
            Put("/v1/offers/offer-flagoff", new { fee = 33 }));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.EditCallCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // A5 — reject (happy + negatives)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A5_Reject_Forwards_To_Upstream_And_Returns_200()
    {
        var fake = new FakeOfferMutationClient
        {
            RejectResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake);

        // Reject is offer-scoped upstream — no routing-index resolution needed.
        var resp = await ClientActor(factory, "sami-client").PostAsync(
            "/v1/offers/rana-offer/reject", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The gateway forwarded the actor (the request-owning client) + offer id.
        fake.LastRejectActor.Should().Be("sami-client");
        fake.LastRejectOfferId.Should().Be("rana-offer");
    }

    [Fact]
    public async Task A5_Reject_NotOwner_Returns_403_VerbatimNot500()
    {
        var fake = new FakeOfferMutationClient
        {
            RejectResult = new OfferMutationResult { Status = OfferMutationStatus.NotOwner },
        };
        using var factory = NewFactory(fake);

        var resp = await ClientActor(factory, "mallory-client").PostAsync(
            "/v1/offers/rana-offer/reject", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-owned");
    }

    [Fact]
    public async Task A5_Reject_AlreadyRejected_Returns_409_Verbatim()
    {
        var fake = new FakeOfferMutationClient
        {
            RejectResult = new OfferMutationResult { Status = OfferMutationStatus.Conflict },
        };
        using var factory = NewFactory(fake);

        var resp = await ClientActor(factory, "sami-client").PostAsync(
            "/v1/offers/rana-offer/reject", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-pending");
    }

    [Fact]
    public async Task A5_Reject_PhantomOffer_Returns_404()
    {
        var fake = new FakeOfferMutationClient
        {
            RejectResult = new OfferMutationResult { Status = OfferMutationStatus.NotFound },
        };
        using var factory = NewFactory(fake);

        var resp = await ClientActor(factory, "sami-client").PostAsync(
            "/v1/offers/phantom-offer/reject", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A5_Reject_AsJeeber_Is403_AtCapabilityGate_NeverReachesUpstream()
    {
        // Reject is a CLIENT action (offer.reject keyed {client}). A jeeber caller is
        // rejected at the L2 capability gate; the upstream is NEVER called.
        var fake = new FakeOfferMutationClient
        {
            RejectResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake);

        var resp = await JeeberActor(factory, "kamal-jeeber").PostAsync(
            "/v1/offers/rana-offer/reject", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fake.RejectCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A5_Reject_FlagOff_Returns_503()
    {
        var fake = new FakeOfferMutationClient
        {
            RejectResult = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        using var factory = NewFactory(fake, offerUpstream: false);

        var resp = await ClientActor(factory, "sami-client").PostAsync(
            "/v1/offers/rana-offer/reject", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.RejectCallCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static HttpRequestMessage Put(string url, object body)
        => new(HttpMethod.Put, url) { Content = JsonContent.Create(body) };

    private static WebApplicationFactory<Program> NewFactory(
        IOfferServiceClient fake, bool offerUpstream = true)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", offerUpstream ? "true" : "false" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
                });
            });

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    private static HttpClient JeeberActor(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    /// <summary>
    /// Test double for offer-service exercising only the edit/reject seams. Records
    /// the forwarded call so the gateway's requestId resolution, dollars→cents
    /// conversion, actor forwarding, and partial-edit field omission are asserted.
    /// Every other interface member throws — these routes must not call them.
    /// </summary>
    private sealed class FakeOfferMutationClient : IOfferServiceClient
    {

        // offerlist-fix: GET-offers list seam. These fakes exercise the
        // accept/mutation paths, not listing — return empty.
        public Task<IReadOnlyList<OfferWire>> ListForRequestAsync(
            string actingUserId, string requestId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OfferWire>>(System.Array.Empty<OfferWire>());
        public OfferMutationResult? EditResult { get; init; }
        public OfferMutationResult? RejectResult { get; init; }

        public int EditCallCount { get; private set; }
        public string? LastEditActor { get; private set; }
        public string? LastEditRequestId { get; private set; }
        public string? LastEditOfferId { get; private set; }
        public long? LastEditFeeCents { get; private set; }
        public int? LastEditEtaMinutes { get; private set; }
        public string? LastEditNote { get; private set; }
        public int? LastEditMaxEdits { get; private set; }

        public int RejectCallCount { get; private set; }
        public string? LastRejectActor { get; private set; }
        public string? LastRejectOfferId { get; private set; }

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId,
            long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
        {
            EditCallCount++;
            LastEditActor = actingUserId;
            LastEditRequestId = requestId;
            LastEditOfferId = offerId;
            LastEditFeeCents = feeCents;
            LastEditEtaMinutes = etaMinutes;
            LastEditNote = note;
            LastEditMaxEdits = maxEdits;
            return Task.FromResult(EditResult!);
        }

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
        {
            RejectCallCount++;
            LastRejectActor = actingUserId;
            LastRejectOfferId = offerId;
            return Task.FromResult(RejectResult!);
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
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
