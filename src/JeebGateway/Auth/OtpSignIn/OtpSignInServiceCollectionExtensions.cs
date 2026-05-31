using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

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
        // ApplicationId has a stable default; BaseUrl is allowed to be empty
        // until ServiceOTPApi:BaseUrl is configured (the typed client below
        // throws on first use if it is missing, not at startup, mirroring the
        // lazy-config policy in ServiceClientExtensions).

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

        // -------------------------------------------------------------------
        // Real downstream adapters (T-BE-001 production wiring).
        //
        // Both are typed HttpClients bound to their configured BaseUrl and the
        // org-standard Polly resilience pipeline (retry + circuit-breaker +
        // per-attempt timeout) — identical policy to ServiceClientExtensions so
        // OTP sign-in inherits the same retry/timeout/breaker behaviour as the
        // rest of the BFF. Registered via AddHttpClient<TInterface,TImpl>, which
        // is what ConfigureTestServices RemoveAll<>()+AddSingleton<>() overrides
        // in OtpServiceWebAppFactory, so the dev/test fakes still win.
        // -------------------------------------------------------------------

        // user-management phone-identity find-or-create (sibling story T-BE-001a).
        // Replaces the former fail-closed NotConfigured shim with a live adapter.
        services
            .AddHttpClient<IUserManagementPhoneIdentityClient, UserManagementPhoneIdentityClient>(
                (sp, http) => BindBaseAddress(
                    http,
                    sp.GetRequiredService<IOptions<UserManagementApiOptions>>().Value.BaseUrl))
            .AddResilienceHandler("otp-signin-user-management", ConfigureStandardResilience);

        // one-time-password send/validate. The adapter injects TimeProvider
        // (to synthesise the audit-locked 300s ExpiresAt) and ServiceOtpApiOptions
        // (for the ApplicationId), both resolved from DI by the typed client.
        services
            .AddHttpClient<IServiceOtpClient, ServiceOtpClient>(
                (sp, http) => BindBaseAddress(
                    http,
                    sp.GetRequiredService<IOptions<ServiceOtpApiOptions>>().Value.BaseUrl))
            .AddResilienceHandler("otp-signin-one-time-password", ConfigureStandardResilience);

        return services;
    }

    /// <summary>
    /// Binds <paramref name="baseUrl"/> as the client's BaseAddress with a
    /// trailing slash (so relative paths like <c>api/OTP/send</c> resolve under
    /// the prefix) and a 30-second hard timeout. Mirrors
    /// <c>ServiceClientExtensions.BindBaseAddress</c>. An empty BaseUrl is left
    /// unset — the typed client then throws on first dispatch (lazy-config
    /// policy) rather than failing the whole host at startup.
    /// </summary>
    private static void BindBaseAddress(HttpClient http, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// The org-standard outbound resilience pipeline, identical to
    /// <c>ServiceClientExtensions.ConfigureStandardResilience</c>:
    ///   - Retry: 3 attempts, exponential backoff (200 ms base) + jitter.
    ///   - Circuit breaker: 50% failure ratio over a 30-second window
    ///     (min 10 throughput), 30-second break.
    ///   - Timeout: 10 seconds per attempt.
    /// </summary>
    private static void ConfigureStandardResilience(ResiliencePipelineBuilder<HttpResponseMessage> b)
    {
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        });

        b.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        b.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
        });
    }
}
