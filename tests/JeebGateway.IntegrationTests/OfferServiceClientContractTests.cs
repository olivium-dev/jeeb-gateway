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
        var client = ClientCapturing(
            HttpStatusCode.OK,
            $$"""
              {"request":{"id":"{{RequestId}}","status":"accepted","accepted_offer_id":"{{OfferId}}","chat_thread_id":"thread-1"},
               "accepted_offer":{"id":"{{OfferId}}","jeeber_id":"{{UserId}}","fee_cents":1500,"eta_minutes":25,"status":"accepted"},
               "rejected_offer_ids":["aaa","bbb"],"chat_thread_id":"thread-1","otp_code":"1234"}
              """,
            (req, _) => captured = req);

        var result = await client.AcceptAsync(UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.AcceptedOfferId.Should().Be(OfferId);
        result.ChatThreadId.Should().Be("thread-1");
        result.OtpCode.Should().Be("1234");
        result.RejectedOfferIds.Should().BeEquivalentTo(new[] { "aaa", "bbb" });
        captured!.Headers.GetValues("Idempotency-Key").Should().ContainSingle()
            .Which.Should().Be("idem-key-12345678");
    }

    [Fact]
    public async Task AcceptWithStatusAsync_Maps_200_To_Accepted_With_Envelope()
    {
        HttpRequestMessage? captured = null;
        var client = ClientCapturing(
            HttpStatusCode.OK,
            $$"""
              {"accepted_offer":{"id":"{{OfferId}}"},
               "rejected_offer_ids":["aaa"],"chat_thread_id":"thread-7","otp_code":"9876"}
              """,
            (req, _) => captured = req);

        var result = await client.AcceptWithStatusAsync(
            UserId, RequestId, OfferId, "idem-key-12345678", CancellationToken.None);

        result.Status.Should().Be(OfferAcceptStatus.Accepted);
        result.Envelope.Should().NotBeNull();
        result.Envelope!.AcceptedOfferId.Should().Be(OfferId);
        result.Envelope.ChatThreadId.Should().Be("thread-7");
        result.Envelope.OtpCode.Should().Be("9876");
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
}
