using System.Net;
using System.Text;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.UnitTests;

// W2/c2-2 (G2): the discriminated Try* reads must tell a GENUINE 2xx-empty apart from a
// degraded upstream, so the fee guards can fail closed instead of silently skipping.
public class OfferServiceClientReadResultTests
{
    private const string ActorId = "3c1d0f4e-2b7a-4c55-9d10-8f6e5a4b3c2d";
    private const string RequestId = "7e0e2c9a-1111-4222-8333-944445555666";
    private const string OfferId = "0b6f2d1e-6f2a-4c1e-9b7d-2a51c0a1b2c3";
    private const string WinnerId = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    // Byte-mirrors offer-service serialize/1 at 75b327e (envelope + BOTH id keys).
    private const string RequestOffersFixture = """
{"offers":[{"id":"0b6f2d1e-6f2a-4c1e-9b7d-2a51c0a1b2c3","request_id":"7e0e2c9a-1111-4222-8333-944445555666",
"actor_id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","jeeber_id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff",
"fee_cents":1500,"eta_minutes":25,"note":null,"status":"pending","edits_count":0,
"created_at":"2026-08-25T00:00:00Z","updated_at":"2026-08-25T00:00:00Z","withdrawn_at":null}]}
""";

    // Same row with the canonical actor_id REMOVED — the cold-reconciliation shape the
    // accept guard must still resolve a GUID winner from.
    private const string RequestOffersAliasOnlyFixture = """
{"offers":[{"id":"0b6f2d1e-6f2a-4c1e-9b7d-2a51c0a1b2c3","request_id":"7e0e2c9a-1111-4222-8333-944445555666",
"jeeber_id":"6f9619ff-8b86-d011-b42d-00cf4fc964ff",
"fee_cents":1500,"eta_minutes":25,"note":null,"status":"pending","edits_count":0,
"created_at":"2026-08-25T00:00:00Z","updated_at":"2026-08-25T00:00:00Z","withdrawn_at":null}]}
""";

    // ---- TryListForRequestAsync -------------------------------------------------

    [Fact]
    public async Task TryListForRequestAsync_ReturnsOk_OnGenuineEmpty2xx()
    {
        var client = NewClient(Json(HttpStatusCode.OK, """{"offers":[]}"""));

        var result = await client.TryListForRequestAsync(ActorId, RequestId, CancellationToken.None);

        Assert.False(result.Degraded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TryListForRequestAsync_ReturnsDegraded_OnNon2xx()
    {
        var client = NewClient(Json(HttpStatusCode.ServiceUnavailable, """{"error":"upstream down"}"""));

        var result = await client.TryListForRequestAsync(ActorId, RequestId, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TryListForRequestAsync_ReturnsDegraded_OnTransportFault()
    {
        var client = NewClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.TryListForRequestAsync(ActorId, RequestId, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.Empty(result.Items);
    }

    // ---- TryListOffersForJeeberAsync --------------------------------------------

    [Fact]
    public async Task TryListOffersForJeeberAsync_ReturnsOk_OnGenuineEmpty2xx()
    {
        // Contract §4.2 is a BARE array; the decode also tolerates the envelope.
        var client = NewClient(Json(HttpStatusCode.OK, "[]"));

        var result = await client.TryListOffersForJeeberAsync(WinnerId, null, CancellationToken.None);

        Assert.False(result.Degraded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TryListOffersForJeeberAsync_ReturnsDegraded_OnNon2xx()
    {
        var client = NewClient(Json(HttpStatusCode.ServiceUnavailable, """{"error":"upstream down"}"""));

        var result = await client.TryListOffersForJeeberAsync(WinnerId, "pending", CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TryListOffersForJeeberAsync_ReturnsDegraded_OnTransportFault()
    {
        var client = NewClient(_ => throw new HttpRequestException("connection refused"));

        var result = await client.TryListOffersForJeeberAsync(WinnerId, null, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.Empty(result.Items);
    }

    // ---- OD-C2-2 pre-deploy check AS A TEST (fixture only, no live legs) --------

    [Fact]
    public async Task RequestOffersWire_CarriesJeeberId_ForColdPathWinnerResolution()
    {
        var client = NewClient(Json(HttpStatusCode.OK, RequestOffersFixture));

        var result = await client.TryListForRequestAsync(ActorId, RequestId, CancellationToken.None);

        Assert.False(result.Degraded);
        var offer = Assert.Single(result.Items);
        Assert.Equal(OfferId, offer.Id);
        Assert.Equal(RequestId, offer.RequestId);
        Assert.Equal(1500, offer.FeeCents);
        // The c2-1 hard-403 cannot fire on a healthy cold path: the winner parses as a GUID.
        Assert.True(Guid.TryParse(offer.JeeberId, out var winner));
        Assert.Equal(Guid.Parse(WinnerId), winner);
    }

    [Fact]
    public async Task RequestOffersWire_ResolvesJeeberId_FromDeprecatedAliasAlone()
    {
        var client = NewClient(Json(HttpStatusCode.OK, RequestOffersAliasOnlyFixture));

        var result = await client.TryListForRequestAsync(ActorId, RequestId, CancellationToken.None);

        Assert.False(result.Degraded);
        var offer = Assert.Single(result.Items);
        Assert.True(Guid.TryParse(offer.JeeberId, out var winner));
        Assert.Equal(Guid.Parse(WinnerId), winner);
    }

    // ---- harness ----------------------------------------------------------------

    private static OfferServiceClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> script)
        => new(new HttpClient(new ScriptedHandler(script))
        {
            BaseAddress = new Uri("http://offer-service.test/")
        });

    private static Func<HttpRequestMessage, HttpResponseMessage> Json(HttpStatusCode status, string body)
        => _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    // Scripted stub: returns the canned response or throws the scripted transport fault.
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _script;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script)
        {
            _script = script;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_script(request));
    }
}
