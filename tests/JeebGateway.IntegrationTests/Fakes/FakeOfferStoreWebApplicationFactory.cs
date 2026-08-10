using System.Linq;
using JeebGateway.Availability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that swaps the gateway's
/// <see cref="IPendingOffersStore"/> for the test-owned
/// <see cref="FakePendingOffersStore"/>.
///
/// GW3 / W3.5(c): before this batch, <c>Program.cs</c> registered an in-memory offer
/// store as a concrete singleton and selected it as <see cref="IPendingOffersStore"/>
/// whenever <c>FeatureFlags:UseUpstream:Offer</c> was false, so a bare
/// <c>WebApplicationFactory&lt;Program&gt;</c> silently handed every test a working
/// offer ledger. The gateway no longer ships one — offer-service is the ledger of
/// record — so a test that needs an offer ledger must now supply it, and does so here.
///
/// This is the honest shape: the fixture double is owned by the fixture, and it is
/// visible in the test's own type which store it is running against.
/// </summary>
public class FakeOfferStoreWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureTestServices(UseFakeOfferStore);

    /// <summary>
    /// The same swap, exposed for tests that already build their own factory with
    /// <c>WithWebHostBuilder</c> and only need the store added to it.
    /// <c>ConfigureTestServices</c> (not <c>ConfigureServices</c>) so the override runs
    /// after <c>Program.cs</c>'s own registrations regardless of hosting model.
    /// </summary>
    public static void UseFakeOfferStore(IServiceCollection services)
    {
        services.RemoveAll<IPendingOffersStore>();
        services.AddSingleton<FakePendingOffersStore>();
        services.AddSingleton<IPendingOffersStore>(
            sp => sp.GetRequiredService<FakePendingOffersStore>());

        // F1 — swap a generous-balance wallet double so the new wallet guards don't
        // trip existing offer tests (the real client's upstream is down in tests).
        services.RemoveAll<SwServiceWalletClient>();
        services.AddScoped<SwServiceWalletClient>(_ => new FakeWalletClient());

        // D2 — same rationale as the wallet double: the new fail-closed range guard on offer
        // submit must not silently disable every unrelated offer test.
        InRangeGeoFixture.UseInRangePresence(services);
    }
}

/// <summary>
/// D2: the offer route now rejects a request the jeeber cannot be proven to be near, so a
/// fixture that seeds offers needs a presence row with coordinates. Mirrors the wallet double
/// above — a guard added for production must not silently disable every unrelated offer test.
/// </summary>
public static class InRangeGeoFixture
{
    /// <summary>Jeeber position AND the pickup point the seeded requests use: distance 0.</summary>
    public const double Lat = 33.5138;

    public const double Lng = 36.2765;

    /// <summary>A seeded catalog tier id (3 km radius) so the tier always resolves.</summary>
    public const string TierId = "urgent";

    public static void UseInRangePresence(IServiceCollection services)
    {
        // A default, never an override: a caller that already supplied its own delivery
        // double keeps it, whatever order it registered in.
        var existing = services.LastOrDefault(
            d => d.ServiceType == typeof(JeebGateway.Services.Clients.IDeliveryServiceClient));
        if (existing?.ImplementationInstance?.GetType().Assembly == typeof(InRangeGeoFixture).Assembly)
        {
            return;
        }

        services.RemoveAll<JeebGateway.Services.Clients.IDeliveryServiceClient>();
        services.AddSingleton<JeebGateway.Services.Clients.IDeliveryServiceClient>(
            new AlwaysOnlineNearbyPresenceClient());
    }
}

/// <summary>Presence double that reports every jeeber online at <see cref="InRangeGeoFixture"/>.</summary>
internal sealed class AlwaysOnlineNearbyPresenceClient : FakeDeliveryPresenceClient
{
    public override Task<JeebGateway.Services.Clients.JeeberAvailabilityUpstream?> GetAvailabilityAsync(
        string jeeberId, CancellationToken ct)
        => Task.FromResult<JeebGateway.Services.Clients.JeeberAvailabilityUpstream?>(
            new JeebGateway.Services.Clients.JeeberAvailabilityUpstream
            {
                JeeberId = jeeberId,
                Online = true,
                VehicleType = "car",
                Zone = "downtown",
                Lat = InRangeGeoFixture.Lat,
                Lng = InRangeGeoFixture.Lng,
                LastSeenAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
}
