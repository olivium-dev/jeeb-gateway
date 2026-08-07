using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.service.ServicePushNotification;
using Xunit;

namespace JeebGateway.IntegrationTests;

// Pins the PrepareRequest X-Api-Key hook (ServicePushNotificationClient.ApiKey.cs):
// relay #22 guards register/delete behind the key the topic seam already sends.
public sealed class PushRelayApiKeyHeaderTests
{
    private const string Key = "test-internal-key";

    private static RegisterRequest Register() => new()
    {
        User_id = "user-1",
        Fcm_token = "fcm-token-1",
        Device_id = "device-1",
    };

    private static ServicePushNotificationClient Client(
        HeaderCapturingHandler handler, string? internalApiKey)
    {
        return new ServicePushNotificationClient("http://push.test/", new HttpClient(handler))
        {
            InternalApiKey = internalApiKey,
        };
    }

    [Fact]
    public async Task RegisterDevice_SendsApiKeyHeader_WhenKeyConfigured()
    {
        var handler = new HeaderCapturingHandler();

        await Client(handler, Key).Register_deviceAsync(Register());

        handler.ApiKeyValuesPerRequest.Should().ContainSingle()
            .Which.Should().Equal(Key);
    }

    [Fact]
    public async Task DeleteByDeviceAndUser_SendsApiKeyHeader_WhenKeyConfigured()
    {
        var handler = new HeaderCapturingHandler();

        await Client(handler, Key).Delete_device_by_device_and_userAsync(
            new DeleteByDeviceAndUserRequest { User_id = "user-1", Device_id = "device-1" });

        handler.ApiKeyValuesPerRequest.Should().ContainSingle()
            .Which.Should().Equal(Key);
    }

    [Fact]
    public async Task DeleteByUser_SendsApiKeyHeader_WhenKeyConfigured()
    {
        var handler = new HeaderCapturingHandler();

        await Client(handler, Key).Delete_all_devices_by_userAsync(
            new DeleteByUserRequest { User_id = "user-1" });

        handler.ApiKeyValuesPerRequest.Should().ContainSingle()
            .Which.Should().Equal(Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RegisterDevice_OmitsApiKeyHeader_WhenKeyNotConfigured(string? key)
    {
        var handler = new HeaderCapturingHandler();

        await Client(handler, key).Register_deviceAsync(Register());

        handler.ApiKeyValuesPerRequest.Should().ContainSingle()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task TopicSend_StillSendsExactlyOneApiKeyValue_NotDuplicatedByTheHook()
    {
        var handler = new HeaderCapturingHandler();

        await Client(handler, Key).Send_notification_to_topicAsync(
            "jeeb_jeebers", new SentPayloadToTopicRequest { Payload = new { title = "t" } });

        handler.ApiKeyValuesPerRequest.Should().ContainSingle()
            .Which.Should().Equal(Key);
    }

    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public List<string[]> ApiKeyValuesPerRequest { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKeyValuesPerRequest.Add(request.Headers.TryGetValues("X-Api-Key", out var values)
                ? values.ToArray()
                : Array.Empty<string>());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"message\":\"ok\",\"timestamp\":\"2026-08-07T00:00:00Z\"}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
