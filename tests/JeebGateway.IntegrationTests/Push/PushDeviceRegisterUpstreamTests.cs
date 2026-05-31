using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Push;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Push;

/// <summary>
/// T-backend-022 push DB wiring: when <c>FeatureFlags:UseUpstream:Push</c> is
/// set, <c>POST /push/devices</c> must hit the real push-notification service
/// (<c>PUT /api/v1/register</c>, which upserts into the Postgres
/// <c>push_notification</c> table) via <see cref="IPushNotificationClient"/>,
/// NOT the in-memory <see cref="IDeviceTokenStore"/>.
///
/// The real FastAPI service isn't bootstrapped in CI, so we substitute a
/// recording fake for <see cref="IPushNotificationClient"/> and assert the
/// controller dispatches to it. The fake's failure mode exercises the
/// negative path (downstream error surfaces, in-memory store untouched).
/// </summary>
public class PushDeviceRegisterUpstreamTests
{
    [Fact]
    public async Task RegisterDevice_With_Push_Flag_On_Calls_Upstream_Not_InMemory()
    {
        // Happy path: flag on → the typed client (real push DB write path)
        // receives the registration; the legacy in-memory store stays empty.
        var fake = new RecordingPushClient();
        var factory = NewFactory(fake, pushFlag: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-upstream");

        var resp = await client.PostAsJsonAsync("/push/devices", new { platform = "fcm", token = "tok-real" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        fake.Calls.Should().ContainSingle();
        var sent = fake.Calls.Single();
        sent.UserId.Should().Be("user-upstream");
        sent.FcmToken.Should().Be("tok-real");
        sent.DeviceId.Should().NotBeNullOrWhiteSpace("upstream PK requires a device_id");

        // The in-memory store must NOT have been written on the upstream path.
        var inMemory = factory.Services.GetRequiredService<IDeviceTokenStore>();
        (await inMemory.GetForUserAsync("user-upstream", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterDevice_With_Push_Flag_Off_Uses_InMemory_Store()
    {
        // Control: flag off → legacy in-memory path, upstream never touched.
        var fake = new RecordingPushClient();
        var factory = NewFactory(fake, pushFlag: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-legacy");

        var resp = await client.PostAsJsonAsync("/push/devices", new { platform = "fcm", token = "tok-legacy" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.Calls.Should().BeEmpty("flag off must not reach the push service");

        var inMemory = factory.Services.GetRequiredService<IDeviceTokenStore>();
        (await inMemory.GetForUserAsync("user-legacy", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task RegisterDevice_With_Push_Flag_On_Surfaces_Downstream_Failure()
    {
        // Negative path: the push service returns an error → the gateway must
        // not swallow it (no false NoContent). EnsureSuccessStatusCode throws,
        // which the host renders as a 5xx.
        var fake = new RecordingPushClient { Throw = true };
        var factory = NewFactory(fake, pushFlag: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-fail");

        var resp = await client.PostAsJsonAsync("/push/devices", new { platform = "fcm", token = "tok-fail" });

        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(500,
            "a downstream push-service failure must not be reported as success");
        fake.Calls.Should().ContainSingle("the controller did attempt the upstream call");
    }

    [Fact]
    public async Task RegisterDevice_With_Push_Flag_On_Still_Requires_Identity()
    {
        // Identity gate runs before the upstream call — an unauthenticated
        // caller must never reach the push DB write path.
        var fake = new RecordingPushClient();
        var factory = NewFactory(fake, pushFlag: true);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/push/devices", new { platform = "fcm", token = "tok-x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fake.Calls.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(IPushNotificationClient pushClient, bool pushFlag)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Push", pushFlag ? "true" : "false");
            builder.ConfigureServices(services =>
            {
                var existing = services.Single(d => d.ServiceType == typeof(IPushNotificationClient));
                services.Remove(existing);
                services.AddSingleton(pushClient);
            });
        });
    }

    private sealed class RecordingPushClient : IPushNotificationClient
    {
        public List<RegisterDeviceUpstreamRequest> Calls { get; } = new();
        public bool Throw { get; init; }

        public Task RegisterDeviceAsync(RegisterDeviceUpstreamRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (Throw)
            {
                throw new HttpRequestException("simulated push-service 500");
            }
            return Task.CompletedTask;
        }
    }
}
