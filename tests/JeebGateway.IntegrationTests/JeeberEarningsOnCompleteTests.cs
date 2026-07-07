using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// jeeber-earnings-on-complete — the money regression this fix closes.
///
/// ROOT CAUSE: with <c>FeatureFlags:UseUpstream:Delivery=true</c> (the live gateway
/// config) the handover OTP verify returns early into
/// <c>VerifyOtpViaDeliveryServiceAsync</c>, which — before this fix — NEVER created a
/// settlement row. The only settlement-creating code lived on the flag-OFF in-memory
/// branch, stranded behind the early return. So a completed COD delivery credited the
/// jeeber NOTHING: no settlement row, <c>POST /api/v1/payments/cod/record</c> 404'd,
/// no wallet <c>cash_settlement</c> ledger entry, $0 earnings.
///
/// THE FIX: on completion (OTP verify → Done, and the customer PATCH → Done) the
/// gateway fires <c>ISettlementService.SettleOnCompletionAsync</c>, which credits the
/// assigned jeeber using the SERVER-AUTHORITATIVE COD amount from the delivery row
/// (<see cref="DeliveryRequest.AcceptedFee"/>, BR-16) — no manual "record cash" step,
/// no client-supplied amount. Idempotent / exactly-once.
///
/// These tests drive the UPSTREAM compose path (Delivery=true) with the delivery + OTP
/// NSwag clients swapped for in-process fakes (same harness as
/// <c>S09HandoverIdempotentReverifyTests</c>), so no live Go/Elixir upstream is needed.
/// </summary>
public class JeeberEarningsOnCompleteTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string RecipientPhone = "+962799123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";

    // Standard tier on a 2,000,000 USD agreed fee.
    private const decimal AcceptedFee = 2_000_000m;
    private const decimal ExpectedCommission = 200_000m;  // 2_000_000 * 0.10
    private const decimal ExpectedInsurance = 0m;
    private const decimal ExpectedTotal = ExpectedCommission;

    /// <summary>
    /// KEYSTONE: an OTP verify that completes the handover on the flag-ON upstream path
    /// CREDITS the jeeber — a settled settlement row is created with the
    /// server-authoritative amount, a wallet ledger entry is posted, and the earnings
    /// summary reflects the gross. Before the fix this produced NO row and $0 earnings.
    /// </summary>
    [Fact]
    public async Task Otp_Verify_Completion_Credits_Jeeber_And_Surfaces_Earnings()
    {
        var delivery = SuccessfulVerifyClient();
        await using var factory = UpstreamFactory(delivery);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory, AcceptedFee);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });
        verify.StatusCode.Should().Be(HttpStatusCode.OK, "the handover completes on the upstream path");

        // (1) A settlement row now exists — server-authoritative amount, jeeber credited.
        var store = factory.Services.GetRequiredService<ISettlementStore>();
        var settlement = await store.GetByDeliveryAsync(deliveryId, default);
        settlement.Should().NotBeNull("completion must create the gateway settlement row (the regression)");
        settlement!.State.Should().Be(SettlementState.Settled);
        settlement.JeeberId.Should().Be(jeeberId);
        settlement.GoodsCost.Should().Be(AcceptedFee, "BR-16: the amount is sourced server-side from the delivery row, not a client body");
        settlement.Commission.Should().Be(ExpectedCommission);
        settlement.Insurance.Should().Be(ExpectedInsurance);
        settlement.Total.Should().Be(ExpectedTotal);
        settlement.PaymentMethod.Should().Be(SettlementService.PaymentMethodCash);
        settlement.LedgerEntryId.Should().NotBeNullOrEmpty("the wallet cash_settlement credit was posted");

        // (2) Earnings now reflect the credit (gross = the server-authoritative amount).
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var earnings = await jeeber.GetAsync($"/api/earnings/summary?from={from}&to={to}");
        earnings.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await earnings.Content.ReadAsStringAsync());
        var totals = doc.RootElement.GetProperty("totals");
        totals.GetProperty("gross").GetDecimal().Should().Be(AcceptedFee);
        totals.GetProperty("commission").GetDecimal().Should().Be(ExpectedCommission);
        totals.GetProperty("net").GetDecimal().Should().Be(AcceptedFee - ExpectedCommission);
        totals.GetProperty("currency").GetString().Should().Be("USD");
    }

    /// <summary>
    /// The downstream cascade the regression broke: with a settlement row now created on
    /// completion, <c>POST /api/v1/payments/cod/record</c> — which 404'd when no row
    /// existed — succeeds and batches the COD.
    /// </summary>
    [Fact]
    public async Task Completion_Enables_Cod_Record_That_Previously_404d()
    {
        var delivery = SuccessfulVerifyClient();
        await using var factory = UpstreamFactory(delivery);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory, AcceptedFee);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode }))
            .EnsureSuccessStatusCode();

        var record = await jeeber.PostAsJsonAsync("/api/v1/payments/cod/record", new { deliveryId }, Json);
        // The settlement row created on completion unblocks the COD record (was 404 before the fix).
        record.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    /// <summary>
    /// Exactly-once credit: firing the completion settlement TWICE (e.g. OTP verify then
    /// the customer PATCH → Done) creates a single settled row and does not double-post
    /// the wallet ledger — the second call short-circuits on the already-settled row.
    /// </summary>
    [Fact]
    public async Task SettleOnCompletion_Is_Idempotent_No_Double_Credit()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var (deliveryId, jeeberId) = await SeedDoneWithFeeAsync(factory, AcceptedFee);

        var svc = factory.Services.GetRequiredService<ISettlementService>();

        var first = await svc.SettleOnCompletionAsync(deliveryId, default);
        first.Outcome.Should().Be(SettlementOutcome.Settled);
        first.Settlement!.GoodsCost.Should().Be(AcceptedFee);
        var firstLedgerId = first.Settlement.LedgerEntryId;
        firstLedgerId.Should().NotBeNullOrEmpty();

        var second = await svc.SettleOnCompletionAsync(deliveryId, default);
        second.Outcome.Should().Be(SettlementOutcome.AlreadySettled,
            "a second completion must not create a second settlement or re-credit the jeeber");
        second.Settlement!.Id.Should().Be(first.Settlement.Id);
        second.Settlement.LedgerEntryId.Should().Be(firstLedgerId, "the ledger credit is posted exactly once");
    }

    /// <summary>
    /// Guard the server-authoritative source (BR-16): the credited goods cost is the
    /// delivery row's agreed fee, resolved server-side — there is no request body on the
    /// completion path at all.
    /// </summary>
    [Fact]
    public async Task SettleOnCompletion_Uses_Server_Authoritative_Amount_From_Delivery_Row()
    {
        await using var factory = new WebApplicationFactory<Program>();
        const decimal fee = 750_000m;
        var (deliveryId, _) = await SeedDoneWithFeeAsync(factory, fee);

        var svc = factory.Services.GetRequiredService<ISettlementService>();
        var result = await svc.SettleOnCompletionAsync(deliveryId, default);

        result.Outcome.Should().Be(SettlementOutcome.Settled);
        result.Settlement!.GoodsCost.Should().Be(fee, "the amount comes from DeliveryRequest.AcceptedFee, not a caller input");
        result.Settlement.Commission.Should().Be(112_500m); // 750_000 * 0.15
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static ConfigurableDeliveryClient SuccessfulVerifyClient() => new()
    {
        VerifyOutcome = _ => new DeliveryHandoverVerifyResult
        {
            DeliveryId = "overwritten",
            Verified = true,
            Status = CanonicalDeliveryStatus.Done
        }
    };

    private WebApplicationFactory<Program> UpstreamFactory(ConfigurableDeliveryClient delivery)
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
            });
        });

    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorWithFeeAsync(
        WebApplicationFactory<Program> factory, decimal fee)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"earn-client-{Guid.NewGuid()}";
        var jeeberId = $"earn-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.TrySetAcceptedFeeAsync(created.Id, fee, default)).Should().BeTrue();
        (await store.SetStatusAsync(created.Id, RequestStatus.AtDoor, default)).Should().BeTrue();
        return (created.Id, jeeberId);
    }

    private static async Task<(string deliveryId, string jeeberId)> SeedDoneWithFeeAsync(
        WebApplicationFactory<Program> factory, decimal fee)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"earn-client-{Guid.NewGuid()}";
        var jeeberId = $"earn-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel"
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.TrySetAcceptedFeeAsync(created.Id, fee, default)).Should().BeTrue();
        (await store.SetStatusAsync(created.Id, CanonicalDeliveryStatus.Done, default)).Should().BeTrue();
        return (created.Id, jeeberId);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    /// <summary>Delivery-service double: the verify hop returns a configurable result; all else is loud.</summary>
    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

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
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
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
