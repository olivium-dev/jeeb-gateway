using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// T-BE-001 / JEB-471 — DI wiring for the OTP sign-in path. Registered from
/// <c>Program.cs</c> in the new <c>// === OTP sign-in (JEB-471) ===</c> band.
/// </summary>
public static class OtpSignInServiceCollectionExtensions
{
    public static IServiceCollection AddJeebOtpSignIn(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options + startup validation. AC5: SigningKey MUST come from env;
        // we reject < 32 bytes at startup so misconfigured deploys never
        // get past readiness.
        services
            .AddOptions<JeebJwtOptions>()
            .Bind(configuration.GetSection(JeebJwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<GatewayRateLimitOptions>()
            .Bind(configuration.GetSection(GatewayRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<UserManagementApiOptions>()
            .Bind(configuration.GetSection(UserManagementApiOptions.SectionName))
            // BaseUrl is allowed to be empty until the sibling T-BE-001a lands;
            // the NotConfigured fallback below fails the request closed.
            ;

        services
            .AddOptions<ServiceOtpApiOptions>()
            .Bind(configuration.GetSection(ServiceOtpApiOptions.SectionName));

        // Phone primitives — singletons (stateless).
        services.TryAddSingleton<IPhoneNormalizer, LibPhoneNumberPhoneNormalizer>();
        services.TryAddSingleton<IPhoneHasher,     BcryptPhoneHasher>();

        // Rate limiter — singleton, partitioned state per phone/IP.
        services.TryAddSingleton<IOtpRequestRateLimiter, SlidingMinuteOtpRequestRateLimiter>();

        // Refresh-token family store — singleton (MVP in-memory).
        services.TryAddSingleton<IRefreshTokenFamilyStore, InMemoryRefreshTokenFamilyStore>();

        // JWT issuer — singleton (HS512 signing creds cached).
        services.TryAddSingleton<IJeebJwtIssuer, JeebJwtIssuer>();

        // Default user-management client: a fail-closed shim used until the
        // sibling T-BE-001a (POST /api/users/phone-identity/find-or-create on
        // olivium-dev/user-management) is wired. Production wiring replaces
        // this registration with the NSwag-generated UserManagementClient.
        // Tests replace it with FakeUserManagementClient via ConfigureTestServices.
        services.TryAddSingleton<IUserManagementPhoneIdentityClient,
                                 NotConfiguredUserManagementPhoneIdentityClient>();

        // IServiceOtpClient does NOT get a default registration: in production
        // the NSwag-generated ServiceOTPClient adapter MUST be wired (or the
        // gateway fails at startup with a clear DI error). Tests register
        // FakeOneTimePasswordClient via ConfigureTestServices.

        return services;
    }
}
