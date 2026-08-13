using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Push;
using JeebGateway.service.ServicePushNotification;
using JeebGateway.Services.Dispatch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>gwdbx W3-14 — the dispatcher re-pointed onto ServicePushNotificationClient behind
/// FeatureFlags:PushDispatchMode: ships at local, identical at local, key verbatim upstream.</summary>
public class PushDispatchRepointW314Tests
{
    private const string Template = "jeeb.delivery.completed";
    private static readonly Guid Recipient = Guid.Parse("6b2b0d4f-98a1-4e5a-9a1a-2f1c0a7a5b31");

    // -------- The shipped default -------------------------------------------------

    [Fact]
    public void PushDispatchMode_Defaults_To_Local()
    {
        new GwdbxMigrationOptions().PushDispatchMode.Should().Be("local");
        new GwdbxMigrationOptions().PushDispatch.Should().Be(GwdbxMigrationPhase.Local);
        GwdbxMigrationOptions.IsKnown(new GwdbxMigrationOptions().PushDispatchMode).Should().BeTrue();
    }

    [Fact]
    public async Task Booted_Gateway_Binds_PushDispatchMode_As_Local()
    {
        await using var factory = new WebApplicationFactory<Program>();
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IOptionsMonitor<GwdbxMigrationOptions>>()
            .CurrentValue.PushDispatch.Should().Be(GwdbxMigrationPhase.Local,
                "W3-14 ships inert; the flip is a separate config change");

        // The new optional ctor args must still resolve out of the real container.
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IJeebNotificationDispatcher>()
            .Should().BeOfType<JeebNotificationDispatcher>();
    }

    // -------- local rung: byte-identical to today ---------------------------------

    [Fact]
    public async Task At_Local_The_Dispatch_Is_Identical_To_The_PreW314_Code_Path()
    {
        var parameters = new Dictionary<string, string> { ["requestId"] = "req-77" };

        // The pre-W3-14 shape: the 4-arg constructor, no ladder and no service client.
        var legacyPush = new CapturingPushNotificationService();
        var legacy = new JeebNotificationDispatcher(
            new StaticNotificationTemplateRenderer(), legacyPush,
            new InMemoryNotificationDispatchOutbox(),
            NullLogger<JeebNotificationDispatcher>.Instance);
        await legacy.DispatchAsync(Template, "en", parameters, Recipient, "notif:req-77", default);

        // The W3-14 shape at the shipped default, with the service client WIRED.
        var localPush = new CapturingPushNotificationService();
        var wire = new CapturingHandler();
        var today = NewDispatcher(new GwdbxMigrationOptions(), localPush, wire);
        var result = await today.DispatchAsync(Template, "en", parameters, Recipient, "notif:req-77", default);

        localPush.Sent.Should().ContainSingle().Which.Should().Be(legacyPush.Sent.Single());
        localPush.Sent.Single().Data.Should().BeSameAs(parameters);
        result.Status.Should().Be(NotificationDispatchStatus.Delivered);
        result.WasDeduplicated.Should().BeFalse();
        wire.Requests.Should().BeEmpty("\"local\" must not touch push-notification at all");
    }

    // -------- upstream-authority rung: the §4.1 Idempotency-Key rider ---------------

    [Fact]
    public async Task At_UpstreamAuthority_The_Idempotency_Key_Reaches_The_Push_Client_Unchanged()
    {
        const string key = "notif:delivery-completed:req-77";
        var localPush = new CapturingPushNotificationService();
        var wire = new CapturingHandler();
        var dispatcher = NewDispatcher(
            new GwdbxMigrationOptions { PushDispatchMode = "upstream-authority" }, localPush, wire);

        var result = await dispatcher.DispatchAsync(
            Template, "en", new Dictionary<string, string> { ["requestId"] = "req-77" },
            Recipient, key, default);

        result.Status.Should().Be(NotificationDispatchStatus.Delivered);
        localPush.Sent.Should().BeEmpty("the in-gateway stack must not also send — that is D1");

        var sent = wire.Requests.Should().ContainSingle().Subject;
        sent.Path.Should().Be("/api/v1/sent-payload/user/" + Recipient.ToString("D"));
        sent.IdempotencyKey.Should().Be(key, "the key push-notification dedups on must be verbatim");
        sent.Body.Should().Contain("\"requestId\":\"req-77\"");
    }

    [Fact]
    public async Task The_Idempotency_Key_Does_Not_Leak_Onto_The_Next_Dispatch()
    {
        var wire = new CapturingHandler();
        var dispatcher = NewDispatcher(
            new GwdbxMigrationOptions { PushDispatchMode = "upstream-authority" },
            new CapturingPushNotificationService(), wire);
        var parameters = new Dictionary<string, string> { ["requestId"] = "req-77" };

        await dispatcher.DispatchAsync(Template, "en", parameters, Recipient, "notif:first", default);
        await dispatcher.DispatchAsync(Template, "en", parameters, Recipient, null, default);

        wire.Requests.Select(r => r.IdempotencyKey).Should().Equal("notif:first", null);
        ServicePushNotificationClient.CurrentIdempotencyKey.Should().BeNull("the scope must unwind");
    }

    // -------- helpers ---------------------------------------------------------------

    private static JeebNotificationDispatcher NewDispatcher(
        GwdbxMigrationOptions options, CapturingPushNotificationService push, CapturingHandler wire) =>
        new(new StaticNotificationTemplateRenderer(), push,
            new InMemoryNotificationDispatchOutbox(),
            NullLogger<JeebNotificationDispatcher>.Instance,
            Options.Create(options),
            new ServicePushNotificationClient("http://push.test", new HttpClient(wire)));

    private sealed class CapturingPushNotificationService : IPushNotificationService
    {
        public List<PushNotificationRequest> Sent { get; } = new();

        public Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.FromResult(new PushDeliveryResult(
                request.UserId, request.Trigger, PushDeliveryOutcome.Delivered, 0));
        }
    }

    private sealed record WireCall(string Path, string? IdempotencyKey, string Body);

    /// <summary>Captures what actually left the generated client, headers included.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<WireCall> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues(
                ServicePushNotificationClient.IdempotencyHeader, out var values)
                ? values.Single()
                : null;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new WireCall(request.RequestUri!.AbsolutePath, key, body));

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"message\":\"ok\",\"timestamp\":\"2026-08-13T00:00:00+00:00\"}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
