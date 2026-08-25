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
    }
}
