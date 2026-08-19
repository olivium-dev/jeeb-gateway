using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class GatewayDirectPushDispatchGuardHandlerTests
{
    [Theory]
    [InlineData("api/v1/sent-payload/device/device-1")]
    [InlineData("api/v1/sent-payload/user/user-1")]
    [InlineData("api/v1/sent-payload/broadcast")]
    [InlineData("api/v1/sent-payload/topic/jeeb_jeebers")]
    public async Task Disabled_BlocksDirectDispatchWithoutCallingPushService(string path)
    {
        var downstream = new RecordingHandler();
        using var client = Client(downstream);

        using var response = await client.PostAsJsonAsync(path, new { payload = new { title = "test" } });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        downstream.RequestCount.Should().Be(0);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(GatewayDirectPushDispatchGuardHandler.DisabledProblemCode);
    }

    [Theory]
    [InlineData("PUT", "api/v1/register")]
    [InlineData("DELETE", "api/v1/register/by-user")]
    [InlineData("GET", "health")]
    [InlineData("GET", "api/v1/sent-payload/idempotency/stale")]
    [InlineData("POST", "api/v1/sent-payload/idempotency/key/resolve")]
    public async Task Disabled_AllowsNonDispatchPushOperations(string method, string path)
    {
        var downstream = new RecordingHandler();
        using var client = Client(downstream);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task DirectDispatchCannotBeReenabledByConfiguration()
    {
        var downstream = new RecordingHandler();
        using var client = Client(downstream);

        using var response = await client.PostAsJsonAsync(
            "api/v1/sent-payload/user/user-1",
            new { payload = new { title = "test" } });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        downstream.RequestCount.Should().Be(0);
    }

    private static HttpClient Client(HttpMessageHandler downstream)
    {
        var guard = new GatewayDirectPushDispatchGuardHandler
        {
            InnerHandler = downstream,
        };
        return new HttpClient(guard) { BaseAddress = new Uri("http://push.test/") };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new { message = "ok" }),
            });
        }
    }
}
