using System;
using System.Collections.Generic;
using System.Linq;
using JeebGateway.Availability;
using JeebGateway.Financials;
using JeebGateway.Financials.Holds;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(UseFakeOfferStore);
    }

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

        // W3 — same rationale as F1/D2, for holds (default ON): without the intent-KV and
        // two-phase doubles every submit would fail closed on E5/E6 in unrelated tests.
        FundedWalletFixture.UseHoldDoubles(services);
    }

    /// <summary>W3/T2 — the shared c1 exposure/holds host: ONE <see cref="FakeWalletHoldEngine"/>
    /// ledger behind the hold client AND the netted balance read, plus the intent store.</summary>
    /// <param name="engine">The ledger; also the default source of the wallet client.</param>
    /// <param name="holdsEnabled"><c>Holds:Enabled</c> — Layer B admission when true, the Layer A
    /// aggregate check when false (the rollout/rollback switch).</param>
    /// <param name="maxLiveOffersPerJeeber">Overrides <c>Offers:MaxLiveOffersPerJeeber</c> so the
    /// cap test does not have to submit 20 offers.</param>
    /// <param name="commissionEnabled"><c>CommissionCollection:Enabled</c> — stays false except in
    /// the capture-on-accept test; money movement is owner-gated (c1b).</param>
    /// <param name="intentStore">Supply one to assert on intent records / drive FailNextWrite.</param>
    /// <param name="walletClient">Supply the gated variant for the barrier tests; defaults to the
    /// engine's own netted client.</param>
    /// <param name="timeProvider">Replaces the host TimeProvider so sweeper tests stay wall-clock-free.</param>
    public static WebApplicationFactory<Program> NewWalletGuardFactory(
        FakeWalletHoldEngine engine,
        bool holdsEnabled,
        int? maxLiveOffersPerJeeber = null,
        bool commissionEnabled = false,
        FakeHoldIntentStore? intentStore = null,
        SwServiceWalletClient? walletClient = null,
        TimeProvider? timeProvider = null,
        string failMode = "fail-closed",
        IEnumerable<KeyValuePair<string, string?>>? extraConfig = null)
    {
        var wallet = walletClient ?? engine.NewWalletClient();
        var intents = intentStore ?? new FakeHoldIntentStore();

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            { "WalletGuard:FailMode", failMode },
            { "FeatureFlags:UseUpstream:Offer", "true" },
            { "Holds:Enabled", holdsEnabled ? "true" : "false" },
            { "CommissionCollection:Enabled", commissionEnabled ? "true" : "false" },
        };
        if (maxLiveOffersPerJeeber is int cap)
        {
            settings["Offers:MaxLiveOffersPerJeeber"] = cap.ToString();
        }
        if (extraConfig is not null)
        {
            foreach (var pair in extraConfig) settings[pair.Key] = pair.Value;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
            {
                UseFakeOfferStore(services);

                services.RemoveAll<SwServiceWalletClient>();
                services.AddScoped<SwServiceWalletClient>(_ => wallet);

                services.RemoveAll<IWalletCommissionDebitClient>();
                services.AddSingleton<IWalletCommissionDebitClient>(engine);

                services.RemoveAll<IHoldIntentStore>();
                services.AddSingleton(intents);
                services.AddSingleton<IHoldIntentStore>(sp => sp.GetRequiredService<FakeHoldIntentStore>());

                if (timeProvider is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(timeProvider);
                }
            });
        });
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
