using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Notifications;

/// <summary>
/// The composition-root half that <see cref="NotificationOwnerClientTests"/> never covered:
/// those tests hand-build the client, so they stayed green while merge 262a682 (merge-all
/// sweep 2026-08-15) took the mainline side of Program.cs and dropped the DI lines 94c2b63
/// had added. INotificationOwnerClient then shipped registered nowhere and
/// JeebNotificationsController (POST /api/notifications) threw at activation.
/// </summary>
public sealed class NotificationOwnerWiringTests
{
    private const string NotificationBaseUrl = "http://127.0.0.1:65011";

    // The suite's own WebApplicationFactory shadow registers a TestNotificationOwnerClient,
    // which is the second reason the drop was invisible. Only the FRAMEWORK factory
    // exercises the real production composition.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory() =>
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ServiceNotificationClient:BaseUrl", NotificationBaseUrl);
                builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
            });

    [Fact]
    public void Container_resolves_the_notification_owner_client()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<INotificationOwnerClient>()
            .Should().BeOfType<NotificationOwnerClient>(
                "POST /api/notifications activates a controller that takes it by constructor");
    }

    [Fact]
    public void Owner_http_client_is_named_and_bound_to_the_notification_base_url()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(NotificationOwnerClient.HttpClientName);

        http.BaseAddress.Should().NotBeNull();
        http.BaseAddress!.ToString().Should().Be(NotificationBaseUrl + "/");
    }

    [Fact]
    public void Credential_handler_is_registered_for_the_owner_pipeline()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<NotificationServiceCredentialHandler>()
            .Should().NotBeNull();
    }
}
