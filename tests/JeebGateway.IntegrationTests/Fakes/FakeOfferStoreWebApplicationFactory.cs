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

        // F1 — the real ServiceWalletClient points at a wallet-service that isn't
        // running in tests; swap a generous-balance double so the new wallet guards
        // don't trip existing offer tests that never intended to exercise them.
        services.RemoveAll<SwServiceWalletClient>();
        services.AddScoped<SwServiceWalletClient>(_ => new FakeWalletClient());
    }
}
