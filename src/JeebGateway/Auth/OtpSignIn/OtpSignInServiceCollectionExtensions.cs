using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// T-BE-001 / JEB-471 — DI wiring for the OTP sign-in path. Registered from
/// <c>Program.cs</c> in the new <c>// === OTP sign-in (JEB-471) ===</c> band.
/// </summary>
public static class OtpSignInServiceCollectionExtensions
{
    /// <summary>
    /// PR #32 review B3 — refuses any SigningKey starting with this prefix
    /// when the host environment is NOT Development. The dev key shipped in
    /// <c>appsettings.Development.json</c> satisfies <c>[MinLength(64)]</c>
    /// but must never be the active key in QA / staging / production.
    /// </summary>
    public const string DevOnlySigningKeyPrefix = "dev-only-";

    public static IServiceCollection AddJeebOtpSignIn(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        // Options + startup validation. AC5: SigningKey MUST come from env;
        // data-annotation MinLength(64) is the baseline; ValidateOnStart fires
        // the dev-only guard below before the host accepts traffic.
        services
            .AddOptions<JeebJwtOptions>()
            .Bind(configuration.GetSection(JeebJwtOptions.SectionName))
            .ValidateDataAnnotations()
            // PR #32 review B3 — fail-on-startup if a dev-shaped signing key
            // or pepper is active outside Development. Catches the
            // appsettings.Development.json values being accidentally inherited.
            .Validate(
                opts => hostEnvironment.IsDevelopment()
                        || !opts.SigningKey.StartsWith(DevOnlySigningKeyPrefix, StringComparison.Ordinal),
                $"JeebJwt:SigningKey must not start with '{DevOnlySigningKeyPrefix}' outside the " +
                "Development environment. Load the production key from env / sealed secret. " +
                "(PR #32 review B3.)")
            .Validate(
                opts => hostEnvironment.IsDevelopment()
                        || !opts.PhonePepper.StartsWith(DevOnlySigningKeyPrefix, StringComparison.Ordinal),
                $"JeebJwt:PhonePepper must not start with '{DevOnlySigningKeyPrefix}' outside the " +
                "Development environment. Load the production pepper from env / sealed secret. " +
                "(PR #32 review B1+B3 symmetry — a known pepper lets an attacker pre-compute the " +
                "phone-hash for the entire +961xxxxxxxx keyspace.)")
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
        // PR #32 review B1: replaced BcryptPhoneHasher (random salt → no
        // correlation) with HMAC-SHA256(pepper, phone) for deterministic
        // hashes. Pepper is bound from JeebJwt:PhonePepper (env-only).
        services.TryAddSingleton<IPhoneHasher,     HmacShaPhoneHasher>();

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
