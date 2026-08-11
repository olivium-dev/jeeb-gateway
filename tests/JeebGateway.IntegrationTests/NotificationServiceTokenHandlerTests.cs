using System.Net;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class NotificationServiceTokenHandlerTests
{
    [Fact]
    public async Task ConfiguredToken_IsSentOnEveryRequest()
    {
        var downstream = new RecordingHandler();
        using var client = Client("cutover-token-1", downstream);

        using var response = await client.GetAsync("notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.LastHeaderValues.Should().Equal("cutover-token-1");
    }

    [Fact]
    public async Task UnsetToken_LeavesRequestUntouched()
    {
        var downstream = new RecordingHandler();
        using var client = Client(token: null, downstream);

        using var response = await client.GetAsync("notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.LastHeaderValues.Should().BeNull();
    }

    [Fact]
    public async Task ExplicitHeader_IsNotOverwritten()
    {
        var downstream = new RecordingHandler();
        using var client = Client("configured-token", downstream);
        using var request = new HttpRequestMessage(HttpMethod.Get, "notifications");
        request.Headers.TryAddWithoutValidation(
            NotificationServiceTokenHandler.HeaderName, "explicit-token");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.LastHeaderValues.Should().Equal("explicit-token");
    }

    private static HttpClient Client(string? token, HttpMessageHandler downstream)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationServiceTokenHandler.ConfigKey] = token,
            })
            .Build();
        var handler = new NotificationServiceTokenHandler(config)
        {
            InnerHandler = downstream,
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://notification.test/") };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string[]? LastHeaderValues { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastHeaderValues = request.Headers.TryGetValues(
                NotificationServiceTokenHandler.HeaderName, out var values)
                ? values.ToArray()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
