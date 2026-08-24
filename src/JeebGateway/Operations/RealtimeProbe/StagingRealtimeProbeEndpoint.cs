using JeebGateway.Realtime;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace JeebGateway.Operations.RealtimeProbe;

internal static class StagingRealtimeProbeEndpoint
{
    internal const string Route = "/internal/ops/staging/realtime-probe-descriptor";

    internal static IServiceCollection AddStagingRealtimeProbe(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!environment.IsStaging())
        {
            return services;
        }

        var realtime = configuration
            .GetSection(RealtimeGuardianOptions.SectionName)
            .Get<RealtimeGuardianOptions>() ?? new RealtimeGuardianOptions();

        services
            .AddOptions<RealtimeProbeOptions>()
            .Bind(configuration.GetSection(RealtimeProbeOptions.SectionName))
            .Validate(
                options => string.Equals(
                    options.MintKeyFile,
                    RealtimeProbeOptions.RequiredMintKeyFile,
                    StringComparison.Ordinal),
                $"{RealtimeProbeOptions.SectionName}:MintKeyFile must be the dedicated "
                + $"mounted-secret path {RealtimeProbeOptions.RequiredMintKeyFile} in Staging.")
            .Validate(
                options => IsDistinctSigningPath(options.MintKeyFile, configuration),
                "The staging realtime probe mint-key file must be distinct from all "
                + "gateway JWT, Guardian, and membership-ticket key files.")
            .Validate(
                _ => RealtimeProbeCredentialConfigurationGuard
                    .HasExactStagingAuthorities(realtime),
                "The staging realtime probe requires empty inline Guardian material, "
                + "the exact dedicated Guardian and membership-ticket secret files, "
                + "issuer live_comm, tenant jeeb, and the exact public WSS URL.")
            .ValidateOnStart();

        services.TryAddSingleton<IRealtimeProbeRequestAuthenticator,
            RealtimeProbeRequestAuthenticator>();
        services.TryAddSingleton<IRealtimeProbeRedisClient>(provider =>
            new StackExchangeRealtimeProbeRedisClient(
                () => provider.GetService<IConnectionMultiplexer>()));
        services.TryAddSingleton<IRealtimeProbeReplayStore,
            RedisRealtimeProbeReplayStore>();
        services.TryAddSingleton<IRealtimeProbeCredentialIssuer,
            RealtimeProbeCredentialIssuer>();
        services.TryAddSingleton<IRealtimeProbeCredentialConfigurationGuard,
            RealtimeProbeCredentialConfigurationGuard>();
        services.TryAddSingleton<IRealtimeProbeDescriptorService,
            RealtimeProbeDescriptorService>();

        return services;
    }

    internal static void MapStagingRealtimeProbe(this WebApplication app)
    {
        if (!app.Environment.IsStaging())
        {
            return;
        }

        app.MapPost(Route, HandleAsync)
            // Authentication is the dedicated HMAC contract above. The gateway's
            // user bearer/fallback policy must not become an alternate requirement.
            .AllowAnonymous()
            .WithName("MintStagingRealtimeProbeDescriptor")
            .WithDisplayName("Mint staging realtime probe descriptor")
            .Produces<RealtimeProbeDescriptor>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")
            // This surface is intentionally absent from the public Swagger document.
            // Its producer contract is pinned under src/JeebGateway/contracts/producer.
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        IRealtimeProbeDescriptorService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        if (!await HasEmptyBodyAsync(context.Request, cancellationToken))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "Malformed staging probe request",
                "The staging realtime probe request body must be empty.",
                "staging-realtime-probe-malformed");
        }

        var result = await service.MintAsync(context.Request.Headers, cancellationToken);
        return result.Status switch
        {
            RealtimeProbeMintStatus.Success when result.Descriptor is not null
                => TypedResults.Ok(result.Descriptor),
            RealtimeProbeMintStatus.Malformed => Problem(
                StatusCodes.Status400BadRequest,
                "Malformed staging probe request",
                "The required probe authentication headers are malformed.",
                "staging-realtime-probe-malformed"),
            RealtimeProbeMintStatus.Stale => Problem(
                StatusCodes.Status401Unauthorized,
                "Stale staging probe request",
                "The signed probe request is outside the accepted clock window.",
                "staging-realtime-probe-stale"),
            RealtimeProbeMintStatus.Forbidden => Problem(
                StatusCodes.Status403Forbidden,
                "Invalid staging probe signature",
                "The staging probe signature was rejected.",
                "staging-realtime-probe-forbidden"),
            RealtimeProbeMintStatus.Replay => Problem(
                StatusCodes.Status409Conflict,
                "Staging probe replay rejected",
                "The signed probe nonce was already consumed.",
                "staging-realtime-probe-replay"),
            _ => Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Staging realtime probe unavailable",
                "A complete short-lived realtime descriptor cannot be issued safely.",
                "staging-realtime-probe-unavailable"),
        };
    }

    private static async Task<bool> HasEmptyBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0)
        {
            return false;
        }

        var singleByte = new byte[1];
        return await request.Body.ReadAsync(singleByte, cancellationToken) == 0;
    }

    private static ProblemHttpResult Problem(
        int status,
        string title,
        string detail,
        string code) => TypedResults.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            type: "https://jeeb.dev/errors/" + code);

    private static bool IsDistinctSigningPath(
        string? mintKeyPath,
        IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(mintKeyPath))
        {
            return false;
        }

        var signingPaths = new[]
        {
            configuration["Jwt:SigningKeyFile"],
            configuration["UmJwt:SigningKeyFile"],
            configuration[$"{RealtimeGuardianOptions.SectionName}:GuardianSecretFile"],
            configuration[$"{RealtimeGuardianOptions.SectionName}:MembershipTicketSigningKeyFile"],
        };

        return signingPaths.All(path =>
            string.IsNullOrWhiteSpace(path)
            || !string.Equals(path, mintKeyPath, StringComparison.Ordinal));
    }
}
