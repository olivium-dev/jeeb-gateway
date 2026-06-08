using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// A4 (durable response-binding) contract-seam guard. The rest of the suite stubs
/// <see cref="IDeliveryServiceClient"/>, so the REAL JSON deserialization of the
/// 2xx body of <c>POST /api/v1/deliveries</c> is never exercised. delivery-service
/// (Go, rest.go) keys the echoed row id as <b><c>delivery_id</c></b> — the same
/// snake_case shape as the OTP issue/verify bodies — NOT <c>id</c>.
///
/// Before the fix, <see cref="DeliveryRowUpstream"/> bound its id from
/// <c>JsonPropertyName("id")</c>. Against the real <c>delivery_id</c> body the id
/// read back <c>null</c>, so <c>DurableRequestsStore.PersistSagaAsync</c>'s
/// stability assertion (<c>row.Id == created.Id</c>) threw → 500 on the durable
/// create path → <c>durable_requests</c> rolled back → A4 idempotent replay
/// returned a NEW id.
///
/// These tests drive the REAL <see cref="DeliveryServiceClient"/> against a fake
/// <see cref="HttpMessageHandler"/> returning the LITERAL Go-shaped body, locking
/// the JSON seam so a future rename re-introduces a failing test rather than a
/// silent re-binding regression.
/// </summary>
public class DeliveryCreateRowContractTests
{
    private const string RowId = "req_a4_fixed_001";

    private static CreateDeliveryRowUpstream SampleBody() => new()
    {
        Id = RowId,
        TenantId = "tenant-1",
        ClientId = "client-1",
        TierId = "flash",
        PickupLat = 25.2,
        PickupLng = 55.3,
    };

    [Fact]
    public async Task CreateDeliveryRowAsync_Binds_SnakeCase_delivery_id_From_201_Body()
    {
        // The LITERAL Go 201 body for POST /api/v1/deliveries.
        var client = ClientReturning(
            HttpStatusCode.Created,
            $$"""{"delivery_id":"{{RowId}}","tenant_id":"tenant-1","status":"Ordered"}""");

        var row = await client.CreateDeliveryRowAsync(SampleBody(), CancellationToken.None);

        // The A4 assertion: the id MUST bind (was null/empty before the fix).
        row.Id.Should().NotBeNullOrEmpty();
        row.Id.Should().Be(RowId);
        row.TenantId.Should().Be("tenant-1");
        row.Status.Should().Be("Ordered");
    }

    [Fact]
    public void DeliveryRowUpstream_Deserializes_delivery_id_With_WebDefaults()
    {
        // Direct DTO bind under the SAME options the client uses
        // (JsonSerializerDefaults.Web), independent of the HTTP plumbing.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var row = JsonSerializer.Deserialize<DeliveryRowUpstream>(
            $$"""{"delivery_id":"{{RowId}}","tenant_id":"tenant-1","status":"Ordered"}""",
            options);

        row.Should().NotBeNull();
        row!.Id.Should().NotBeNullOrEmpty();
        row.Id.Should().Be(RowId);
    }

    [Fact]
    public void DeliveryRowUpstream_Falls_Back_To_Legacy_id_Key()
    {
        // Backward-compatible fallback: an older/alternate upstream shape that
        // emits "id" still binds (additive, non-breaking).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var row = JsonSerializer.Deserialize<DeliveryRowUpstream>(
            $$"""{"id":"{{RowId}}","tenant_id":"tenant-1"}""",
            options);

        row.Should().NotBeNull();
        row!.Id.Should().Be(RowId);
    }

    [Theory]
    [InlineData("""{"delivery_id":"req_a4_fixed_001","id":"legacy_loser"}""")]
    [InlineData("""{"id":"legacy_loser","delivery_id":"req_a4_fixed_001"}""")]
    public void DeliveryRowUpstream_Prefers_delivery_id_Over_legacy_id_Regardless_Of_Order(string body)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var row = JsonSerializer.Deserialize<DeliveryRowUpstream>(body, options);

        row.Should().NotBeNull();
        row!.Id.Should().Be(RowId);
    }

    private static DeliveryServiceClient ClientReturning(HttpStatusCode status, string jsonBody)
    {
        var handler = new SingleResponseHandler(status, jsonBody);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://delivery.test/") };
        return new DeliveryServiceClient(http);
    }

    /// <summary>
    /// Returns one fixed status + literal JSON body for every request. The body is
    /// written verbatim so the test asserts against the EXACT bytes delivery-service
    /// emits, not a round-tripped re-serialization.
    /// </summary>
    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _jsonBody;

        public SingleResponseHandler(HttpStatusCode status, string jsonBody)
        {
            _status = status;
            _jsonBody = jsonBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }
}
