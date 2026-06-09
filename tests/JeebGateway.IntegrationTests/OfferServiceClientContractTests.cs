using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Contract-seam guard for the thin-BFF offer-service wire
/// (FeatureFlags:UseUpstream:Offer). offer-service (Elixir/Phoenix, host port
/// 10063) exposes NO OpenAPI document, so <see cref="OfferServiceClient"/> is
/// hand-coded against the routes in
/// <c>offer-service/lib/offer_service_web/router.ex</c>. The rest of the suite
/// uses the in-memory store, so the REAL JSON (snake_case, integer
/// <c>fee_cents</c>) + the conflict-code → exception translation is never
/// otherwise exercised. This suite drives the REAL client two ways:
///
/// <list type="bullet">
///   <item><b>Fake-handler tests</b> (always run, CI-authoritative): feed the
///     LITERAL Elixir-shaped snake_case bodies and lock the JSON seam +
///     dollars↔cents mapping + status collapse + 409/404/403 mapping.</item>
///   <item><b>Live-wire tests</b> (opt-in via <c>JEEB_OFFER_LIVE=1</c>): hit the
///     real upstream at <c>JEEB_OFFER_BASEURL</c> (default
///     http://192.168.2.50:10063) and assert the auth/error envelope the
///     gateway depends on. Skipped by default because CI has no route to the
///     upstream's private network.</item>
/// </list>
/// </summary>
public class OfferServiceClientContractTests
{
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string RequestId = "22222222-2222-2222-2222-222222222222";
    private const string OfferId = "33333333-3333-3333-3333-333333333333";

    // -----------------------------------------------------------------------
    // Fake-handler seam tests (always run)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SubmitAsync_Binds_SnakeCase_FeeCents_And_Status()
    {
        // LITERAL 201 body from offer_controller.ex serialize/1.
        var client = ClientReturning(HttpStatusCode.Created,
            $$"""
              {"id":"{{OfferId}}","request_id":"{{RequestId}}","jeeber_id":"{{UserId}}",
               "fee_cents":1500,"eta_minutes":25,"note":"hi","status":"submitted",
               "edits_count":0,"created_at":"2026-06-01T10:00:00Z","updated_at":null,
               "withdrawn_at":null}
              """);

        var wire = await client.SubmitAsync(UserId, RequestId, 1500, 25, "hi", CancellationToken.None);

        wire.Id.Should().Be(OfferId);
        wire.FeeCents.Should().Be(1500);
        wire.Status.Should().Be("submitted");
        wire.JeeberId.Should().Be(UserId);
    }

    [Fact]
    public async Task SubmitAsync_Forwards_XUserId_And_SnakeCase_Body()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var client = ClientCapturing(
            HttpStatusCode.Created,
            $$"""{"id":"{{OfferId}}","request_id":"{{RequestId}}","jeeber_id":"{{UserId}}","fee_cents":100,"eta_minutes":10,"status":"submitted"}""",
            (req, b) => { captured = req; body = b; });

        await client.SubmitAsync(UserId, RequestId, 100, 10, null, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/v1/requests/{RequestId}/offers");
        captured.Headers.GetValues("x-user-id").Should().ContainSingle().Which.Should().Be(UserId);
        body.Should().Contain("\"fee_cents\":100").And.Contain("\"eta_minutes\":10");
    }

    [Fact]
    public async Task SubmitAsync_Maps_409_To_OfferUpstreamConflictException()
    {
        var client = ClientReturning(HttpStatusCode.Conflict,
            """{"error":{"code":"request_not_open","message":"Request is no longer open"}}""");

        var act = async () =>
            await client.SubmitAsync(UserId, RequestId, 1500, 25, null, CancellationToken.None);

        (await act.Should().ThrowAsync<OfferUpstreamConflictException>())
            .Which.UpstreamCode.Should().Be("request_not_open");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, OfferWithdrawResult.Withdrawn)]
    [InlineData(HttpStatusCode.NoContent, OfferWithdrawResult.Withdrawn)]
    [InlineData(HttpStatusCode.NotFound, OfferWithdrawResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, OfferWithdrawResult.NotOwned)]
    [InlineData(HttpStatusCode.Conflict, OfferWithdrawResult.NotPending)]
    [InlineData(HttpStatusCode.Gone, OfferWithdrawResult.NotPending)]
    public async Task WithdrawAsync_Maps_HttpStatus_To_Outcome(
        HttpStatusCode status, OfferWithdrawResult expected)
    {
        var body = status is HttpStatusCode.OK
            ? $$"""{"id":"{{OfferId}}","request_id":"{{RequestId}}","jeeber_id":"{{UserId}}","fee_cents":100,"eta_minutes":10,"status":"withdrawn"}"""
            : """{"error":{"code":"x","message":"y"}}""";
        var client = ClientReturning(status, body);

        var result = await client.WithdrawAsync(UserId, RequestId, OfferId, CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task AcceptAsync_Binds_Envelope_And_Sends_IdempotencyKey()
    {
        HttpRequestMessage? captured = null;
        // JEB-1474: the accept envelope is ONLY the generic transition outcome —
        // accepted offer id (+ canonical actor_id) and rejected sibling ids. No
        // otp_code / chat_thread_id are emitted by the shared service anymore.
        var client = ClientCapturing(
            HttpStatusCode.OK,
            $$"""
              {"request":{"id":"{{RequestId}}","status":"accepted","accepted_offer_id":"{{OfferId}}"},
               "accepted_offer":{"id":"{{OfferId}}","actor_id":"{{UserId}}","fee_cents":1500,"eta_minutes":25,"status":"accepted"},
               "rejected_offer_ids":["aaa","bbb"]}
              """,
            (req, _) => captured = req);

        var result = await client.AcceptAsync(UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.AcceptedOfferId.Should().Be(OfferId);
        result.JeeberId.Should().Be(UserId);
        result.RejectedOfferIds.Should().BeEquivalentTo(new[] { "aaa", "bbb" });
        captured!.Headers.GetValues("Idempotency-Key").Should().ContainSingle()
            .Which.Should().Be("idem-key-12345678");
    }

    [Fact]
    public async Task AcceptAsync_FallsBack_To_Deprecated_JeeberId_Alias()
    {
        // Backward-compat: until every offer row is backfilled, the deprecated
        // jeeber_id alias may arrive without actor_id; the client must still
        // resolve the winning actor.
        var client = ClientReturning(
            HttpStatusCode.OK,
            $$"""
              {"accepted_offer":{"id":"{{OfferId}}","jeeber_id":"{{UserId}}"},
               "rejected_offer_ids":[]}
              """);

        var result = await client.AcceptAsync(UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.JeeberId.Should().Be(UserId);
    }

    [Fact]
    public async Task AcceptWithStatusAsync_Maps_200_To_Accepted_With_Envelope()
    {
        HttpRequestMessage? captured = null;
        var client = ClientCapturing(
            HttpStatusCode.OK,
            $$"""
              {"accepted_offer":{"id":"{{OfferId}}","actor_id":"{{UserId}}"},
               "rejected_offer_ids":["aaa"]}
              """,
            (req, _) => captured = req);

        var result = await client.AcceptWithStatusAsync(
            UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.Status.Should().Be(OfferAcceptStatus.Accepted);
        result.Envelope.Should().NotBeNull();
        result.Envelope!.AcceptedOfferId.Should().Be(OfferId);
        result.Envelope.JeeberId.Should().Be(UserId);
        result.Envelope.RejectedOfferIds.Should().BeEquivalentTo(new[] { "aaa" });
        captured!.Headers.GetValues("Idempotency-Key").Should().ContainSingle()
            .Which.Should().Be("idem-key-12345678");
        captured.RequestUri!.AbsolutePath.Should().Be(
            $"/api/v1/requests/{RequestId}/offers/{OfferId}/accept");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, OfferAcceptStatus.NotOwner)]
    [InlineData(HttpStatusCode.Gone, OfferAcceptStatus.Expired)]
    [InlineData(HttpStatusCode.Conflict, OfferAcceptStatus.Conflict)]
    [InlineData(HttpStatusCode.NotFound, OfferAcceptStatus.NotFound)]
    public async Task AcceptWithStatusAsync_Forwards_Upstream_Negative_Verbatim(
        HttpStatusCode upstream, OfferAcceptStatus expected)
    {
        // Negative bodies carry an error envelope; the client preserves the
        // status and never throws (so the gateway can map it, not mask it).
        var client = ClientReturning(upstream,
            """{"error":{"code":"some_upstream_code","message":"nope"}}""");

        var result = await client.AcceptWithStatusAsync(
            UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.Status.Should().Be(expected);
        result.Envelope.Should().BeNull();
        result.UpstreamCode.Should().Be("some_upstream_code");
    }

    [Fact]
    public async Task AcceptWithStatusAsync_Tolerates_Empty_Negative_Body()
    {
        var client = ClientReturning(HttpStatusCode.Gone, string.Empty);

        var result = await client.AcceptWithStatusAsync(
            UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.Status.Should().Be(OfferAcceptStatus.Expired);
        result.UpstreamCode.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Adapter mapping seam (dollars↔cents, status collapse) through the store
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UpstreamStore_Maps_Dollars_To_Cents_And_Cents_Back_To_Dollars()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var client = ClientCapturing(
            HttpStatusCode.Created,
            $$"""{"id":"{{OfferId}}","request_id":"{{RequestId}}","jeeber_id":"{{UserId}}","fee_cents":1550,"eta_minutes":25,"status":"submitted"}""",
            (req, b) => { captured = req; body = b; });
        var store = new UpstreamPendingOffersStore(client);

        // $15.50 → 1550 cents on the way out; 1550 cents → $15.50 on the way back.
        var offer = await store.TrySubmitAsync(
            RequestId, UserId, fee: 15.50m, etaMinutes: 25, note: null,
            maxPerRequest: 20, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        body.Should().Contain("\"fee_cents\":1550");
        offer.Fee.Should().Be(15.50m);
        offer.Status.Should().Be(PendingOfferStatus.Pending); // "submitted" collapses to pending
    }

    [Fact]
    public async Task UpstreamStore_Translates_DuplicateConflict_To_DuplicateOfferException()
    {
        var client = ClientReturning(HttpStatusCode.Conflict,
            """{"error":{"code":"offer_already_exists","message":"already offered"}}""");
        var store = new UpstreamPendingOffersStore(client);

        var act = async () => await store.TrySubmitAsync(
            RequestId, UserId, 15m, 25, null, 20, DateTimeOffset.UtcNow, CancellationToken.None);

        await act.Should().ThrowAsync<DuplicateOfferException>();
    }

    // -----------------------------------------------------------------------
    // GW-1 / GW-2: request-mirror bridge + submit error mapping (S07 close-out)
    // -----------------------------------------------------------------------

    private const string ClientId = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public async Task MirrorRequestAsync_Posts_RequestBridge_Body_And_Maps_201_To_Created()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var client = ClientCapturing(
            HttpStatusCode.Created,
            $$"""{"id":"{{RequestId}}","request_id":"{{RequestId}}","client_id":"{{ClientId}}","status":"open"}""",
            (req, b) => { captured = req; body = b; });

        var result = await client.MirrorRequestAsync(UserId, RequestId, ClientId, CancellationToken.None);

        result.Should().Be(RequestMirrorResult.Created);
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/requests");
        body.Should().Contain($"\"request_id\":\"{RequestId}\"")
            .And.Contain($"\"client_id\":\"{ClientId}\"")
            .And.Contain("\"status\":\"open\"");
    }

    [Fact]
    public async Task MirrorRequestAsync_Maps_200_Replay_To_AlreadyMirrored()
    {
        var client = ClientReturning(HttpStatusCode.OK,
            $$"""{"id":"{{RequestId}}","request_id":"{{RequestId}}","client_id":"{{ClientId}}","status":"open"}""");

        var result = await client.MirrorRequestAsync(UserId, RequestId, ClientId, CancellationToken.None);

        result.Should().Be(RequestMirrorResult.AlreadyMirrored);
    }

    [Fact]
    public async Task MirrorRequestAsync_Maps_422_To_ValidationException()
    {
        var client = ClientReturning(HttpStatusCode.UnprocessableEntity,
            """{"error":{"code":"client_id_invalid","message":"bad client"}}""");

        var act = async () => await client.MirrorRequestAsync(UserId, RequestId, "not-a-uuid", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OfferUpstreamValidationException>()).Which;
        ex.Stage.Should().Be("mirror");
        ex.UpstreamStatus.Should().Be(422);
        ex.UpstreamCode.Should().Be("client_id_invalid");
    }

    [Fact]
    public async Task SubmitAsync_Maps_404_To_RequestNotMirroredException()
    {
        // GW-2: the original H1/H2 failure — offer-service 404s because the
        // request was never mirrored. Must NOT raw-throw HttpRequestException
        // (which the global handler turns into a 502); must surface the typed
        // signal so the store can mirror-then-retry.
        var client = ClientReturning(HttpStatusCode.NotFound,
            """{"error":{"code":"request_not_found","message":"unknown request"}}""");

        var act = async () =>
            await client.SubmitAsync(UserId, RequestId, 1500, 25, null, CancellationToken.None);

        (await act.Should().ThrowAsync<OfferRequestNotMirroredException>())
            .Which.RequestId.Should().Be(RequestId);
    }

    [Fact]
    public async Task SubmitAsync_Maps_422_To_ValidationException()
    {
        var client = ClientReturning(HttpStatusCode.UnprocessableEntity,
            """{"error":{"code":"fee_cents_too_low","message":"min 100"}}""");

        var act = async () =>
            await client.SubmitAsync(UserId, RequestId, 1, 25, null, CancellationToken.None);

        (await act.Should().ThrowAsync<OfferUpstreamValidationException>())
            .Which.Stage.Should().Be("submit");
    }

    [Fact]
    public async Task UpstreamStore_OnSubmit404_Mirrors_Then_Retries_Submit_Once()
    {
        // Sequence the real client: submit#1 → 404 (not mirrored),
        // mirror → 201, submit#2 (retry) → 201 created offer.
        var seq = new SequencedHandler(new[]
        {
            // submit #1 — request not mirrored yet
            (HttpMethod.Post, $"/api/v1/requests/{RequestId}/offers", HttpStatusCode.NotFound,
                """{"error":{"code":"request_not_found","message":"unknown"}}"""),
            // mirror — OS-1
            (HttpMethod.Post, "/api/v1/requests", HttpStatusCode.Created,
                $$"""{"id":"{{RequestId}}","request_id":"{{RequestId}}","client_id":"{{ClientId}}","status":"open"}"""),
            // submit #2 — now resolves
            (HttpMethod.Post, $"/api/v1/requests/{RequestId}/offers", HttpStatusCode.Created,
                $$"""{"id":"{{OfferId}}","request_id":"{{RequestId}}","jeeber_id":"{{UserId}}","fee_cents":1500,"eta_minutes":25,"status":"submitted"}"""),
        });
        var client = new OfferServiceClient(new HttpClient(seq) { BaseAddress = new Uri("http://offer-service.test/") });
        var store = new UpstreamPendingOffersStore(client);

        var offer = await store.TrySubmitAsync(
            RequestId, UserId, fee: 15m, etaMinutes: 25, note: null,
            maxPerRequest: 20, at: DateTimeOffset.UtcNow, ct: CancellationToken.None,
            clientId: ClientId);

        offer.Id.Should().Be(OfferId);
        offer.Status.Should().Be(PendingOfferStatus.Pending);
        // Proves the bridge fired: submit, mirror, submit (3 upstream calls).
        seq.Calls.Should().HaveCount(3);
        seq.Calls[0].AbsolutePath.Should().Be($"/api/v1/requests/{RequestId}/offers");
        seq.Calls[1].AbsolutePath.Should().Be("/api/v1/requests");
        seq.Calls[2].AbsolutePath.Should().Be($"/api/v1/requests/{RequestId}/offers");
    }

    [Fact]
    public async Task UpstreamStore_DoesNotLoop_When_Submit404s_After_Mirror()
    {
        // Genuine not-found: even after a successful mirror the submit 404s.
        // The store retries EXACTLY ONCE then lets the 404 surface — no loop.
        var seq = new SequencedHandler(new[]
        {
            (HttpMethod.Post, $"/api/v1/requests/{RequestId}/offers", HttpStatusCode.NotFound,
                """{"error":{"code":"request_not_found","message":"unknown"}}"""),
            (HttpMethod.Post, "/api/v1/requests", HttpStatusCode.Created,
                $$"""{"id":"{{RequestId}}","request_id":"{{RequestId}}","client_id":"{{ClientId}}","status":"open"}"""),
            (HttpMethod.Post, $"/api/v1/requests/{RequestId}/offers", HttpStatusCode.NotFound,
                """{"error":{"code":"request_not_found","message":"still unknown"}}"""),
        });
        var client = new OfferServiceClient(new HttpClient(seq) { BaseAddress = new Uri("http://offer-service.test/") });
        var store = new UpstreamPendingOffersStore(client);

        var act = async () => await store.TrySubmitAsync(
            RequestId, UserId, 15m, 25, null, 20, DateTimeOffset.UtcNow, CancellationToken.None,
            clientId: ClientId);

        await act.Should().ThrowAsync<OfferRequestNotMirroredException>();
        seq.Calls.Should().HaveCount(3); // submit, mirror, submit — then surfaced, no 4th
    }

    [Fact]
    public async Task UpstreamStore_WithoutClientId_Surfaces_404_Without_Mirroring()
    {
        // No clientId threaded (e.g. legacy caller): the store cannot mirror,
        // so the 404 surfaces unchanged after a single submit — no mirror call.
        var seq = new SequencedHandler(new[]
        {
            (HttpMethod.Post, $"/api/v1/requests/{RequestId}/offers", HttpStatusCode.NotFound,
                """{"error":{"code":"request_not_found","message":"unknown"}}"""),
        });
        var client = new OfferServiceClient(new HttpClient(seq) { BaseAddress = new Uri("http://offer-service.test/") });
        var store = new UpstreamPendingOffersStore(client);

        var act = async () => await store.TrySubmitAsync(
            RequestId, UserId, 15m, 25, null, 20, DateTimeOffset.UtcNow, CancellationToken.None);

        await act.Should().ThrowAsync<OfferRequestNotMirroredException>();
        seq.Calls.Should().HaveCount(1); // submit only — never mirrored
    }

    // -----------------------------------------------------------------------
    // Live-wire tests (opt-in)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LiveWire_Submit_Without_XUserId_Is_Unauthorized()
    {
        if (!LiveEnabled(out var baseUrl)) return;

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        using var resp = await http.PostAsync(
            $"api/v1/requests/{RequestId}/offers",
            new StringContent("""{"fee_cents":1500,"eta_minutes":25}""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LiveWire_Withdraw_Unknown_Offer_Maps_To_NotFound()
    {
        if (!LiveEnabled(out var baseUrl)) return;

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var client = new OfferServiceClient(http);

        var result = await client.WithdrawAsync(UserId, RequestId, OfferId, CancellationToken.None);

        result.Should().Be(OfferWithdrawResult.NotFound);
    }

    private static bool LiveEnabled(out string baseUrl)
    {
        baseUrl = Environment.GetEnvironmentVariable("JEEB_OFFER_BASEURL")
                  ?? "http://192.168.2.50:10063";
        return Environment.GetEnvironmentVariable("JEEB_OFFER_LIVE") == "1";
    }

    // -----------------------------------------------------------------------
    // Fake handler plumbing
    // -----------------------------------------------------------------------

    private static OfferServiceClient ClientReturning(HttpStatusCode status, string json)
        => new(new HttpClient(new StubHandler(status, json))
        {
            BaseAddress = new Uri("http://offer-service.test/")
        });

    private static OfferServiceClient ClientCapturing(
        HttpStatusCode status, string json, Action<HttpRequestMessage, string?> capture)
        => new(new HttpClient(new StubHandler(status, json, capture))
        {
            BaseAddress = new Uri("http://offer-service.test/")
        });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;
        private readonly Action<HttpRequestMessage, string?>? _capture;

        public StubHandler(HttpStatusCode status, string json,
            Action<HttpRequestMessage, string?>? capture = null)
        {
            _status = status;
            _json = json;
            _capture = capture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _capture?.Invoke(request, body);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }

    /// <summary>
    /// Replays a fixed script of (method, path, status, json) responses in order,
    /// recording every request URI. Used to prove the GW-1 mirror-then-retry
    /// sequence (submit → 404, mirror → 201, submit → 201) without a live
    /// upstream. Asserts the actual request matches the scripted method+path so a
    /// mis-routed call fails loudly rather than silently consuming the next step.
    /// </summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly (HttpMethod Method, string Path, HttpStatusCode Status, string Json)[] _script;
        private int _index;

        public List<Uri> Calls { get; } = new();

        public SequencedHandler(
            (HttpMethod Method, string Path, HttpStatusCode Status, string Json)[] script)
        {
            _script = script;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!);

            if (_index >= _script.Length)
            {
                throw new InvalidOperationException(
                    $"SequencedHandler exhausted: unexpected extra call #{_index + 1} to " +
                    $"{request.Method} {request.RequestUri!.AbsolutePath}.");
            }

            var step = _script[_index++];
            request.Method.Should().Be(step.Method);
            request.RequestUri!.AbsolutePath.Should().Be(step.Path);

            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                Content = new StringContent(step.Json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
