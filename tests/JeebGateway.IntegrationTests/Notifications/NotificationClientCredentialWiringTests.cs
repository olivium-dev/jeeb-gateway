using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.Notifications;

public sealed class NotificationClientCredentialWiringTests : IDisposable
{
    private static readonly string[] ActiveClientNames =
    [
        "ServiceNotificationClient",
        NotificationOwnerClient.HttpClientName,
        JeebNotificationRecordClient.HttpClientName,
        "db-probe-notification",
    ];

    private readonly string _directory =
        Directory.CreateTempSubdirectory("notification-wiring").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Every_active_notification_pipeline_reads_the_same_mounted_owner_credential()
    {
        const string token = "notification-owner-token-shared-by-all-clients";
        var tokenFile = Path.Combine(_directory, "notification-token");
        await File.WriteAllTextAsync(tokenFile, token);
        var observed = new ConcurrentDictionary<string, string?>();

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ServiceNotificationClient:BaseUrl", "http://notification.test/");
                builder.UseSetting("ServiceNotificationClient:ServiceTokenFile", tokenFile);
                builder.ConfigureServices(services =>
                {
                    foreach (var name in ActiveClientNames)
                    {
                        services.Configure<HttpClientFactoryOptions>(name, options =>
                            options.HttpMessageHandlerBuilderActions.Add(messageBuilder =>
                                messageBuilder.PrimaryHandler = new CaptureHandler(name, observed)));
                    }
                });
            });

        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var name in ActiveClientNames)
            await clients.CreateClient(name).GetAsync("credential-wiring-probe");

        observed.Keys.Should().BeEquivalentTo(ActiveClientNames);
        observed.Values.Should().OnlyContain(value => value == token);
    }

    [Fact]
    public void Conditional_catalog_seeder_pipeline_uses_the_file_backed_handler()
    {
        var program = File.ReadAllText(FindRepositoryFile("src/JeebGateway/Program.cs"));

        program.Should().Contain(
            "seederClient.AddHttpMessageHandler<JeebGateway.Notifications.NotificationServiceTokenHandler>()");
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, relativePath)))
            current = current.Parent;
        return current is null
            ? throw new DirectoryNotFoundException("Could not find the gateway repository root.")
            : Path.Combine(current.FullName, relativePath);
    }

    private sealed class CaptureHandler(
        string name,
        ConcurrentDictionary<string, string?> observed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            observed[name] = request.Headers.TryGetValues(
                NotificationServiceTokenHandler.HeaderName,
                out var values)
                ? values.SingleOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
