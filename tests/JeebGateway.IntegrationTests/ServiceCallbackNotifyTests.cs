using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-341 (batch b02, step 4) — the inbound service callback API,
/// <c>POST /svc-callbacks/notify</c>, exercised over REAL HTTP against the real
/// <c>Program</c> host. The push microservice seam is the only thing substituted
/// (<see cref="RecordingPushClient"/>), so "exactly one push left the gateway" is a
/// counted fact rather than an inference from a log line.
///
/// <para><b>Why the push count is asserted and not the response shape alone.</b> Service
/// callbacks retry. A dedupe that returns <c>wasDeduplicated:true</c> while still dispatching
/// a second push looks perfect in review and produces duplicate notification-shade entries in
/// production. The replay test therefore asserts <see cref="RecordingPushClient.Attempts"/>
/// == 1 across two POSTs with the same key — the negative control for the dedupe.</para>
///
/// <para><b>No auth by design.</b> Per owner ruling of 2026-07-26 there is no authorization
/// between microservices, so every request below is anonymous and must NOT 401. The
/// <c>/internal/health</c> assertion proves the new <c>/svc-callbacks/</c> prefix did not
/// disturb the public liveness probe the deploy health gate reads.</para>
/// </summary>
public sealed class ServiceCallbackNotifyTests
{
    private const string Endpoint = "/svc-callbacks/notify";
    private const string Recipient = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task Valid_Callback_Returns_202_And_Sends_Exactly_One_Push()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        var resp = await client.PostAsync(Endpoint, Json(new
        {
            notificationType = "jeeb.kyc_approved",
            recipientUserId = Recipient,
            locale = "en",
            silent = false,
            idempotencyKey = "kyc-approved:" + Guid.NewGuid(),
            parameters = new Dictionary<string, string>(),
            data = new Dictionary<string, string> { ["entityId"] = "kyc-1" },
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body = await ReadBodyAsync(resp);
        body.EntryId.Should().NotBeNullOrWhiteSpace();
        body.WasDeduplicated.Should().BeFalse();
        body.Status.Should().Be("Queued");

        push.Attempts.Should().Be(1, "one accepted callback dispatches exactly one push");
        // The recipient comes from the callback BODY, not from a caller token — that is the whole
        // reason this endpoint exists and also the accepted risk it carries.
        push.RecipientIds.Should().ContainSingle().Which.Should().Be(Recipient);
    }

    [Theory]
    [InlineData("en", "Offer Updated", "A delivery offer has been updated. Open Jeeb to view the latest details.")]
    [InlineData("ar", "تحديث على العرض", "تم تحديث عرض توصيل. افتح جيب للاطلاع على أحدث التفاصيل.")]
    public async Task OfferUpdated_Callback_Uses_Localized_Copy_OfferLink_And_Deduplicates(
        string locale,
        string expectedTitle,
        string expectedBody)
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();
        var key = $"offer-updated:{locale}:{Guid.NewGuid()}";

        object Payload() => new
        {
            notificationType = "jeeb.offer_updated",
            recipientUserId = Recipient,
            locale,
            silent = false,
            idempotencyKey = key,
            data = new Dictionary<string, string>
            {
                ["offerId"] = "offer-42",
                ["status"] = "expired",
            },
        };

        var first = await client.PostAsync(Endpoint, Json(Payload()));
        var replay = await client.PostAsync(Endpoint, Json(Payload()));

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ReadBodyAsync(replay)).WasDeduplicated.Should().BeTrue();
        push.Attempts.Should().Be(1);

        var sent = push.UserSends.Should().ContainSingle().Which;
        var payload = sent.Payload.Should().BeAssignableTo<IDictionary<string, object?>>().Subject;
        payload["title"].Should().Be(expectedTitle);
        payload["body"].Should().Be(expectedBody);
        payload["type"].Should().Be("jeeb.offer_updated");
        payload["deepLink"].Should().Be("jeeb://offers/offer-42");
        payload["offerId"].Should().Be("offer-42");
        payload["status"].Should().Be("expired");
    }

    [Fact]
    public async Task Unknown_NotificationType_Returns_400_And_Sends_No_Push()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        var resp = await client.PostAsync(Endpoint, Json(new
        {
            notificationType = "jeeb.not_a_real_type",
            recipientUserId = Recipient,
            silent = false,
            idempotencyKey = "unknown-type:" + Guid.NewGuid(),
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Missing_IdempotencyKey_Returns_400_And_Sends_No_Push()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        // The field is REQUIRED because callbacks retry; omitting it must not be treated as
        // "dedup disabled, push anyway".
        var resp = await client.PostAsync(Endpoint, Json(new
        {
            notificationType = "jeeb.kyc_approved",
            recipientUserId = Recipient,
            silent = false,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Blank_RecipientUserId_Returns_400_And_Sends_No_Push()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        var resp = await client.PostAsync(Endpoint, Json(new
        {
            notificationType = "jeeb.kyc_approved",
            recipientUserId = "   ",
            silent = false,
            idempotencyKey = "blank-recipient:" + Guid.NewGuid(),
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Attempts.Should().Be(0);
    }

    /// <summary>
    /// THE NEGATIVE TEST. Same idempotencyKey twice ⇒ second answer is
    /// <c>wasDeduplicated:true</c> carrying the ORIGINAL entryId, and the recorder proves exactly
    /// ONE push left the gateway. If the dedupe silently did not work this is the only assertion
    /// that fails.
    /// </summary>
    [Fact]
    public async Task Replayed_IdempotencyKey_Dedupes_And_Sends_No_Second_Push()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        var key = "replay:" + Guid.NewGuid();
        object Payload() => new
        {
            notificationType = "jeeb.settlement_paid",
            recipientUserId = Recipient,
            locale = "ar",
            silent = false,
            idempotencyKey = key,
            data = new Dictionary<string, string> { ["settlementId"] = "s-9" },
        };

        var first = await client.PostAsync(Endpoint, Json(Payload()));
        var second = await client.PostAsync(Endpoint, Json(Payload()));

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var firstBody = await ReadBodyAsync(first);
        var secondBody = await ReadBodyAsync(second);

        firstBody.WasDeduplicated.Should().BeFalse();
        secondBody.WasDeduplicated.Should().BeTrue();
        secondBody.EntryId.Should().Be(firstBody.EntryId,
            "a replay must answer with the ORIGINAL entry identity, not a fresh one");

        push.Attempts.Should().Be(1,
            "the replay must send NO second push — a duplicate here is a duplicate shade entry in production");
    }

    /// <summary>
    /// The reason the callback is mounted under <c>/svc-callbacks/</c> rather than
    /// <c>/internal/</c>: the API-key middleware gates on the <c>/internal</c> path prefix BEFORE
    /// routing, so it exempts neither <c>[AllowAnonymous]</c> nor <c>[PublicEndpoint]</c>. Mounting
    /// there would tie the callback to a switch that also 401s this probe.
    /// </summary>
    [Fact]
    public async Task Internal_Health_Probe_Still_Returns_200()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/internal/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// b02 step 6b (owner ruling D3 = retire) — <c>jeeb.offer_rejected</c> must be unreachable from
    /// this endpoint. It used to be blocked by a one-entry deny-list for execution-plan correction
    /// C6 (the centre answers 405 for it where every served type answers 422). Step 6b removed the
    /// type from <see cref="JeebGateway.Notifications.JeebNotificationCatalog"/> instead, so it is
    /// now rejected as an UNKNOWN type by the same check that rejects any other non-catalog string.
    /// The observable contract is identical — 400, no push — which is exactly why this test kept
    /// its assertions and only changed its reason.
    /// </summary>
    [Fact]
    public async Task Retired_Unroutable_Type_Is_Rejected()
    {
        var push = new RecordingPushClient();
        await using var factory = CreateFactory(push);
        var client = factory.CreateClient();

        var resp = await client.PostAsync(Endpoint, Json(new
        {
            notificationType = "jeeb.offer_rejected",
            recipientUserId = Recipient,
            silent = false,
            idempotencyKey = "c6:" + Guid.NewGuid(),
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Attempts.Should().Be(0);
    }

    private static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<NotifyBody> ReadBodyAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new NotifyBody(
            root.GetProperty("entryId").GetString() ?? string.Empty,
            root.GetProperty("wasDeduplicated").GetBoolean(),
            root.GetProperty("status").GetString() ?? string.Empty);
    }

    private sealed record NotifyBody(string EntryId, bool WasDeduplicated, string Status);

    private static WebApplicationFactory<Program> CreateFactory(RecordingPushClient push)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Security:RateLimit:Enabled"] = "false",
                    }));

                // Only the push microservice seam is substituted; everything else is the real host.
                builder.ConfigureTestServices(services =>
                    services.AddScoped<ServicePushNotificationClient>(_ => push));
            });
}
