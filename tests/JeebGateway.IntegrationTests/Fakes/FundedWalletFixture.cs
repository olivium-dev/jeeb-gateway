using JeebGateway.Financials;
using JeebGateway.Financials.Holds;
using JeebGateway.service.ServiceWallet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>c2-1 ripple: the offer guards now run for every GUID caller, so a factory whose
/// subject is NOT the wallet guard needs a reachable, funded wallet double.</summary>
public static class FundedWalletFixture
{
    public static void UseFundedWallet(IServiceCollection services)
    {
        services.RemoveAll<ServiceWalletClient>();
        services.AddScoped<ServiceWalletClient>(_ => new FakeWalletClient());
        UseHoldDoubles(services);
    }

    /// <summary>W3 ripple: holds default ON, so the intent KV and the two-phase client need doubles
    /// or every submit/accept fails closed on E5/E6 in tests whose subject is something else.</summary>
    public static void UseHoldDoubles(IServiceCollection services)
    {
        services.RemoveAll<IHoldIntentStore>();
        services.AddSingleton<IHoldIntentStore>(_ => new FakeHoldIntentStore());
        services.RemoveAll<IWalletCommissionDebitClient>();
        services.AddSingleton<IWalletCommissionDebitClient>(_ => new FakeWalletHoldEngine());
    }
}
