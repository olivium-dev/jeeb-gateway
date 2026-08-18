using System.Net;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class NotificationServiceTokenHandlerTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("notification-token-handler").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Mounted_token_is_sent_on_every_request()
    {
        var path = WriteToken("notification-cutover-token-1");
        var downstream = new RecordingHandler();
        using var client = Client(path, downstream);

        using var response = await client.GetAsync("notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.LastHeaderValues.Should().Equal("notification-cutover-token-1");
    }

    [Fact]
    public async Task Mounted_token_is_reloaded_after_rotation_without_restart()
    {
        var path = WriteToken("notification-cutover-token-before");
        var downstream = new RecordingHandler();
        using var client = Client(path, downstream);

        await client.GetAsync("notifications/first");
        downstream.LastHeaderValues.Should().Equal("notification-cutover-token-before");

        await File.WriteAllTextAsync(path, "notification-cutover-token-after\n");
        await client.GetAsync("notifications/second");

        downstream.LastHeaderValues.Should().Equal("notification-cutover-token-after");
    }

    [Fact]
    public async Task Missing_mounted_token_fails_closed_before_network_call()
    {
        var downstream = new RecordingHandler();
        using var client = Client(Path.Combine(_directory, "missing"), downstream);

        var act = () => client.GetAsync("notifications");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing or outside the allowed size*");
        downstream.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Caller_supplied_header_is_replaced_by_mounted_owner_credential()
    {
        var path = WriteToken("notification-mounted-owner-token");
        var downstream = new RecordingHandler();
        using var client = Client(path, downstream);
        using var request = new HttpRequestMessage(HttpMethod.Get, "notifications");
        request.Headers.TryAddWithoutValidation(
            NotificationServiceTokenHandler.HeaderName,
            "caller-controlled-value");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        downstream.LastHeaderValues.Should().Equal("notification-mounted-owner-token");
    }

    private string WriteToken(string token)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, token);
        return path;
    }

    private static HttpClient Client(string tokenFile, HttpMessageHandler downstream)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServiceNotificationClient:ServiceTokenFile"] = tokenFile,
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
        public int CallCount { get; private set; }
        public string[]? LastHeaderValues { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastHeaderValues = request.Headers.TryGetValues(
                NotificationServiceTokenHandler.HeaderName,
                out var values)
                ? values.ToArray()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
