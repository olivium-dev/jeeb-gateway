using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.service.ServicePushNotification;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-344 LIVE positive control. Drives the real <see cref="StateServiceIdempotencyStore"/>
/// against a real jeeb-state-service, because the bug was invisible to every in-process test:
/// <c>WebApplicationFactory</c> resolves <see cref="InMemoryIdempotencyStore"/>, where
/// <c>Inserted</c> has always worked. A green integration suite was not evidence.
///
/// <para>Opt-in — skipped unless <c>JEEB_LIVE_STATE_SERVICE_URL</c> is set, so CI (which has no
/// state-service) stays green. To run it against the MSI dev box, forward the port first:</para>
/// <code>
/// ssh -L 10073:127.0.0.1:10073 &lt;msi&gt;   # state-service is bound LAN-side but firewalled
/// JEEB_LIVE_STATE_SERVICE_URL=http://127.0.0.1:10073 \
///   dotnet test --filter FullyQualifiedName~StateServiceIdempotencyLiveTests
/// </code>
/// </summary>
public class StateServiceIdempotencyLiveTests
{
    private const string UrlVar = "JEEB_LIVE_STATE_SERVICE_URL";

    private static string? LiveUrl =>
        Environment.GetEnvironmentVariable(UrlVar) is { Length: > 0 } u ? u.TrimEnd('/') + "/" : null;

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public StateServiceIdempotencyLiveTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <remarks>
    /// HONESTY NOTE: xunit 2.9 has no dynamic skip and this repo does not reference
    /// Xunit.SkippableFact, so without <c>JEEB_LIVE_STATE_SERVICE_URL</c> this test is a
    /// NO-OP that reports green. A green CI run of this test proves NOTHING. Only a run whose
    /// output contains the "LIVE CONTROL RAN" line below is evidence.
    /// </remarks>
    [Fact]
    public async Task Live_StateService_First_Insert_Is_True_And_Replay_Is_False()
    {
        var url = LiveUrl;
        if (url is null)
        {
            _out.WriteLine($"SKIPPED (no-op): {UrlVar} not set — this result is NOT evidence.");
            return;
        }

        var store = new StateServiceIdempotencyStore(
            new JeebStateServiceClient(url, new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
            NullLogger<StateServiceIdempotencyStore>.Instance);

        // A fresh key every run; short TTL so the live box is not littered.
        var key = $"jebv4-344-live:{Guid.NewGuid():N}";
        _out.WriteLine($"LIVE CONTROL RAN against {url} with fresh key {key}");

        var first = await store.PutOrGetAsync(key, 202, """{"entryId":"first"}""", 300, default);
        var replay = await store.PutOrGetAsync(key, 202, """{"entryId":"second"}""", 300, default);

        first.Inserted.Should().BeTrue(
            "a FRESH key must report the insert it actually performed — this is the assertion "
            + "the pre-fix code could never satisfy against a real state-service");
        first.StatusCode.Should().Be(202);
        first.ResponseBodyJson.Should().Contain("first");

        replay.Inserted.Should().BeFalse("the second write lost the insert-once race");
        replay.ResponseBodyJson.Should().Contain("first")
            .And.NotContain("second", "a replay observes the ORIGINAL stored effect");

        // And a plain read still reports the original, never a phantom insert.
        var read = await store.GetAsync(key, default);
        read.Should().NotBeNull();
        read!.Inserted.Should().BeFalse();
        read.ResponseBodyJson.Should().Contain("first");
    }

    /// <summary>
    /// JEBV4-344 DoD — the endpoint-level proof, run against the REAL state-service store.
    ///
    /// <para><see cref="ServiceCallbackNotifyTests"/> already covers this endpoint, but it resolves
    /// <see cref="InMemoryIdempotencyStore"/> from the host, where <c>Inserted</c> always worked;
    /// that suite stayed green for the whole life of the bug. Here the ONLY substitution besides the
    /// push seam is the opposite one: the real <see cref="StateServiceIdempotencyStore"/> talking to
    /// a real jeeb-state-service — i.e. the production wiring that was broken.</para>
    ///
    /// <para>Positive control: a FRESH key must answer <c>wasDeduplicated:false</c> AND dispatch
    /// (push attempt counted). Negative control: the SAME key replayed must answer
    /// <c>wasDeduplicated:true</c> with the ORIGINAL entryId and dispatch NOTHING.</para>
    /// </summary>
    [Fact]
    public async Task Live_SvcCallback_Notify_Fresh_Key_Dispatches_And_Replay_Dedupes()
    {
        var url = LiveUrl;
        if (url is null)
        {
            _out.WriteLine($"SKIPPED (no-op): {UrlVar} not set — this result is NOT evidence.");
            return;
        }

        const string recipient = "11111111-2222-3333-4444-555555555555";
        var key = $"jebv4-344-dod:{Guid.NewGuid():N}";
        _out.WriteLine($"LIVE CONTROL RAN against {url} with fresh idempotencyKey {key}");

        var push = new LivePushRecorder();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Security:RateLimit:Enabled"] = "false" }));

            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<ServicePushNotificationClient>(_ => push);

                // THE POINT OF THIS TEST: the production idempotency store, on the live service.
                services.AddSingleton<IJeebStateServiceClient>(_ =>
                    new JeebStateServiceClient(url, new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
                services.AddSingleton<IIdempotencyStore>(sp =>
                    new StateServiceIdempotencyStore(
                        sp.GetRequiredService<IJeebStateServiceClient>(),
                        NullLogger<StateServiceIdempotencyStore>.Instance));
            });
        });

        var client = factory.CreateClient();

        StringContent Payload() => new(JsonSerializer.Serialize(new
        {
            notificationType = "jeeb.kyc_approved",
            recipientUserId = recipient,
            locale = "en",
            silent = false,
            idempotencyKey = key,
            data = new Dictionary<string, string> { ["entityId"] = "kyc-jebv4-344" },
        }), Encoding.UTF8, "application/json");

        var first = await client.PostAsync("/svc-callbacks/notify", Payload());
        var firstJson = await first.Content.ReadAsStringAsync();
        _out.WriteLine($"first  -> {(int)first.StatusCode} {firstJson}");

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var firstDoc = JsonDocument.Parse(firstJson);
        firstDoc.RootElement.GetProperty("wasDeduplicated").GetBoolean().Should().BeFalse(
            "a FRESH key is an insert — this is the assertion the pre-fix store could never satisfy "
            + "against a real state-service, because it read the flag back with a GET");
        var entryId = firstDoc.RootElement.GetProperty("entryId").GetString();

        push.Attempts.Should().Be(1, "an accepted first callback must actually dispatch");

        var second = await client.PostAsync("/svc-callbacks/notify", Payload());
        var secondJson = await second.Content.ReadAsStringAsync();
        _out.WriteLine($"replay -> {(int)second.StatusCode} {secondJson}");

        second.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var secondDoc = JsonDocument.Parse(secondJson);
        secondDoc.RootElement.GetProperty("wasDeduplicated").GetBoolean().Should().BeTrue();
        secondDoc.RootElement.GetProperty("entryId").GetString().Should().Be(entryId,
            "a replay answers with the ORIGINAL entry identity");

        push.Attempts.Should().Be(1,
            "the replay must dispatch NOTHING — a second push here is a duplicate shade entry");
    }

    private sealed class LivePushRecorder : ServicePushNotificationClient
    {
        public LivePushRecorder()
            : base("http://push.invalid/", new HttpClient(new NeverCalledHandler())) { }

        public int Attempts { get; private set; }

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new SentPayloadResponse
            {
                Message = "ok",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }

        private sealed class NeverCalledHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("the push seam must not reach the network here");
        }
    }
}
