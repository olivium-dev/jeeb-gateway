using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// fix/status-change-push (AUDIT-B FINDING #1). Same "stranded on the dead in-memory
/// branch" class as the settlement bug: the counterparty <see cref="NotificationTrigger.StatusChange"/>
/// push (courier picked up / on the way / arrived / delivery completed) was only wired
/// into the retired flag-OFF in-memory VerifyOtp branch. On live
/// (<c>FeatureFlags:UseUpstream:Delivery=true</c>) every transition returned 200 with NO
/// push, so customer &amp; jeeber got no delivery-status pushes at all.
///
/// THE FIX wires the existing <c>NotifyOtherPartyAsync</c> counterparty push into BOTH
/// live paths:
/// <list type="bullet">
///   <item><c>PatchStatusViaDeliveryServiceAsync</c> — the real PATCH /status path the
///     app drives for Picked/InTransit/AtDoor/Delivered.</item>
///   <item><c>VerifyOtpViaDeliveryServiceAsync</c> — the flag-ON handover verify → Done.</item>
/// </list>
/// Both are STRICTLY best-effort: a push-composer throw must never turn a committed
/// transition/handover into a 5xx.
///
/// These tests drive the UPSTREAM compose path (Delivery=true) with the delivery + OTP
/// NSwag clients and the push composer swapped for in-process fakes (same harness family
/// as <c>JeeberEarningsOnCompleteTests</c>), so no live Go/Elixir upstream is needed.
///
/// <para><b>⚠️ THIS GUARD HAD INVERTED, and was merged red.</b> It spied on
/// <c>IPushNotificationService</c> — the in-gateway Stack A composer. PR #330 moved the
/// delivery-status category off that stack (it binds <c>InMemoryPushTransport</c>, an
/// in-process queue that delivers nothing and is then counted <c>Delivered</c>) onto
/// <see cref="IDeliveryStatusPushNotifier"/> and the push microservice. All three tests
/// went red on that commit — they were green on its parent — and stayed red on main. A
/// guard demanding the DEAD path is not a safety net: it is standing pressure to re-add
/// the path whose whole defect was that it reported success for a push that never left
/// the process, and "make CI green" is all it takes. The spy is now the live notifier.</para>
///
/// <para><b>The push is FIRE-AND-FORGET</b> (JEBV4-281: awaiting a real push-service round
/// trip in front of the response timed transitions out client-side). So every assertion
/// below waits for the detached task instead of reading the spy straight after the
/// response — a synchronous read here would be a race that fails ~always on a fast box and
/// passes on a slow one.</para>
/// </summary>
public class StatusChangePushTests
{
    private const string RecipientPhone = "+9613123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";

    /// <summary>
    /// How long to wait for the DETACHED push task. Generous because it only ever costs
    /// wall-clock on a genuine failure: the wait polls and returns the moment the push
    /// lands, which on a healthy box is the first 25ms tick.
    /// </summary>
    private static readonly TimeSpan DetachedPushWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// KEYSTONE (PATCH live path): a flag-ON PATCH /status transition that commits
    /// upstream fans a <see cref="NotificationTrigger.StatusChange"/> push to BOTH the
    /// client and the jeeber (the counterparties). Before the fix this path emitted
    /// nothing.
    /// </summary>
    [Fact]
    public async Task PatchStatus_Transition_On_Live_Emits_StatusChange_Push_To_Counterparty()
    {
        var push = new CapturingDeliveryStatusPush();
        var delivery = new ConfigurableDeliveryClient
        {
            TransitionOutcome = to => new DeliveryTransitionUpstream { DeliveryId = "overwritten", Status = to }
        };
        await using var factory = UpstreamFactory(delivery, push);
        var (deliveryId, clientId, jeeberId) = await SeedPickedUpWithJeeberAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "the canonical transition committed upstream");

        (await push.WaitForAttemptsAsync(1, DetachedPushWait))
            .Should().BeTrue("the committed transition must fan a StatusChange push (the regression)");

        var statusPushes = push.Sent.Where(n => n.DeliveryId == deliveryId).ToList();

        statusPushes.Should().NotBeEmpty("the committed transition must fan a StatusChange push (the regression)");
        statusPushes.Should().OnlyContain(n => n.Recipients.Contains(clientId), "the client is a counterparty");
        statusPushes.Should().OnlyContain(n => n.Recipients.Contains(jeeberId), "the jeeber is a counterparty");
        statusPushes.Should().OnlyContain(n => n.Status == CanonicalDeliveryStatus.InTransit,
            "the push carries the fresh upstream target status");
        statusPushes.Should().OnlyContain(n => n.PreviousStatus != n.Status,
            "\"Status changed from X to X.\" is never a true sentence — the caller must snapshot "
            + "the pre-transition status before the store mirror advances the live row in place");
    }

    /// <summary>
    /// KEYSTONE (handover live path): the /otp/verify → Done completion on the flag-ON
    /// upstream path fans the completion <see cref="NotificationTrigger.StatusChange"/>
    /// push to the counterparty. Before the fix the flag-ON compose path returned before
    /// the (in-memory-only) push, so nobody got the "delivery completed" notification.
    /// </summary>
    [Fact]
    public async Task OtpVerify_Completion_On_Live_Emits_Completion_StatusChange_Push()
    {
        var push = new CapturingDeliveryStatusPush();
        var delivery = new ConfigurableDeliveryClient
        {
            VerifyOutcome = _ => new DeliveryHandoverVerifyResult
            {
                DeliveryId = "overwritten",
                Verified = true,
                Status = CanonicalDeliveryStatus.Done
            }
        };
        await using var factory = UpstreamFactory(delivery, push);
        var (deliveryId, clientId, jeeberId) = await SeedAtDoorWithJeeberAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK, "the handover completes on the upstream path");

        (await push.WaitForAttemptsAsync(1, DetachedPushWait))
            .Should().BeTrue("handover completion must fan a StatusChange push");

        var completionPushes = push.Sent.Where(n => n.DeliveryId == deliveryId).ToList();

        completionPushes.Should().NotBeEmpty("handover completion must fan a StatusChange push");
        completionPushes.Should().OnlyContain(n => n.Recipients.Contains(clientId));
        completionPushes.Should().OnlyContain(n => n.Recipients.Contains(jeeberId));
        completionPushes.Should().OnlyContain(n => n.Status == CanonicalDeliveryStatus.Done,
            "the completion push carries the Done terminal status");
    }

    /// <summary>
    /// BEST-EFFORT GUARD: a push composer that THROWS on every send must NOT turn the
    /// committed transition into a 5xx — the transition already committed upstream and
    /// the 200 is authoritative. The push is fire-and-forget observability, never a gate.
    /// </summary>
    [Fact]
    public async Task PatchStatus_Push_Composer_Throw_Does_Not_Fail_The_Transition()
    {
        var push = new CapturingDeliveryStatusPush { ThrowOnSend = true };
        var delivery = new ConfigurableDeliveryClient
        {
            TransitionOutcome = to => new DeliveryTransitionUpstream { DeliveryId = "overwritten", Status = to }
        };
        await using var factory = UpstreamFactory(delivery, push);
        var (deliveryId, _, jeeberId) = await SeedPickedUpWithJeeberAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var patch = await jeeber.PatchAsJsonAsync(
            $"/deliveries/{deliveryId}/status", new { to = CanonicalDeliveryStatus.InTransit });

        patch.StatusCode.Should().Be(HttpStatusCode.OK,
            "a push-composer fault is swallowed best-effort; the committed transition stays a 200");

        (await push.WaitForAttemptsAsync(1, DetachedPushWait))
            .Should().BeTrue("the push WAS attempted (and threw), proving the guard caught it");
        push.Sent.Should().BeEmpty("the throwing composer captured nothing — the attempt is the evidence");
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private WebApplicationFactory<Program> UpstreamFactory(
        ConfigurableDeliveryClient delivery, CapturingDeliveryStatusPush push)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new RecordingOtpClient());
                // Registered SINGLETON on purpose although the real notifier is SCOPED:
                // the controller resolves it from a FRESH scope inside the detached task,
                // and a scoped fake would hand each resolution a different, empty spy.
                services.RemoveAll<IDeliveryStatusPushNotifier>();
                services.AddSingleton<IDeliveryStatusPushNotifier>(push);
            });
        });

    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedPickedUpWithJeeberAsync(
        WebApplicationFactory<Program> factory)
        => await SeedWithJeeberAsync(factory, RequestStatus.PickedUp);

    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedAtDoorWithJeeberAsync(
        WebApplicationFactory<Program> factory)
        => await SeedWithJeeberAsync(factory, RequestStatus.AtDoor);

    private static async Task<(string deliveryId, string clientId, string jeeberId)> SeedWithJeeberAsync(
        WebApplicationFactory<Program> factory, string status)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"push-client-{Guid.NewGuid()}";
        var jeeberId = $"push-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.SetStatusAsync(created.Id, status, default)).Should().BeTrue();
        return (created.Id, clientId, jeeberId);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    /// <summary>
    /// Captures every delivery-status push handed to the notifier; can be told to throw to
    /// prove the caller's best-effort guard catches it.
    ///
    /// <para>Thread-safe and awaitable because the production call site DETACHES the send
    /// onto a background task — the response returns before this is ever invoked.</para>
    /// </summary>
    private sealed class CapturingDeliveryStatusPush : IDeliveryStatusPushNotifier
    {
        private readonly object _gate = new();
        private readonly List<DeliveryStatusPushNotification> _sent = new();
        private int _attempts;

        public bool ThrowOnSend { get; init; }

        public int SendAttempts => Volatile.Read(ref _attempts);

        public IReadOnlyList<DeliveryStatusPushNotification> Sent
        {
            get { lock (_gate) { return _sent.ToList(); } }
        }

        public Task NotifyAsync(DeliveryStatusPushNotification notification, CancellationToken ct)
        {
            if (ThrowOnSend)
            {
                // Count the attempt BEFORE throwing: the assertion that matters is "the push
                // was attempted and the caller swallowed the fault", and a counter bumped
                // only on success cannot distinguish that from "never attempted at all".
                Interlocked.Increment(ref _attempts);
                throw new InvalidOperationException("simulated push composer failure");
            }

            lock (_gate)
            {
                _sent.Add(notification);
            }

            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Waits for the detached push to land, POLLING rather than sleeping a fixed
        /// interval so a slow CI box does not turn a correct fix into a flake. Returns
        /// false on timeout so the caller fails with a domain message instead of this
        /// helper throwing a timeout no reader can interpret.
        /// </summary>
        public async Task<bool> WaitForAttemptsAsync(int attempts, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (SendAttempts < attempts && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            return SendAttempts >= attempts;
        }
    }

    /// <summary>Delivery-service double: the transition + verify hops return configurable results; all else is loud.</summary>
    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<string, DeliveryTransitionUpstream> TransitionOutcome { get; init; }
            = to => throw new DeliveryTransitionException((int)HttpStatusCode.UnprocessableEntity, "transition_not_allowed", null, to, null);

        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            var r = TransitionOutcome(to);
            return Task.FromResult(new DeliveryTransitionUpstream { DeliveryId = deliveryId, Status = r.Status });
        }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            var r = VerifyOutcome(success);
            return Task.FromResult(new DeliveryHandoverVerifyResult
            {
                DeliveryId = deliveryId,
                Verified = r.Verified,
                Status = r.Status
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                Status = CanonicalDeliveryStatus.Done,
                CreatedAt = DateTimeOffset.UtcNow
            });

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
