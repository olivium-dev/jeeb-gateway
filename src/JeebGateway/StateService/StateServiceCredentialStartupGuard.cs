using Microsoft.Extensions.Logging;

namespace JeebGateway.StateService;

/// <summary>
/// Boot-time proof that an armed state credential is actually usable, and a journal line naming
/// the outcome. Opt-in: with <c>JeebStateService__ServiceTokenFile</c> unset this is a no-op.
/// </summary>
public static class StateServiceCredentialStartupGuard
{
    public static void Validate(StateServiceOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            logger.LogInformation("jeeb-state-service: not wired (Enabled={Enabled}, BaseUrl set={HasBaseUrl}).",
                options.Enabled, !string.IsNullOrWhiteSpace(options.BaseUrl));
            return;
        }

        if (!options.HasServiceCredential)
        {
            logger.LogInformation(
                "jeeb-state-service: wired UNAUTHENTICATED — {Key} is not set, so no Authorization header is sent.",
                StateServiceOptionsFactory.ServiceTokenFileKey);
            return;
        }

        try
        {
            StateServiceCredentialHandler.ReadTokenAsync(options.ServiceTokenFile, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "jeeb-state-service: {Key} is armed but the credential could not be loaded from {TokenFile}. Refusing to start rather than serving 500s on every state-backed write (login, idempotency, refresh tokens).",
                StateServiceOptionsFactory.ServiceTokenFileKey, options.ServiceTokenFile);
            throw new InvalidOperationException(
                $"{StateServiceOptionsFactory.ServiceTokenFileKey} is armed but the credential at "
                + $"'{options.ServiceTokenFile}' is missing or invalid.", ex);
        }

        logger.LogInformation(
            "jeeb-state-service: credential ARMED from {TokenFile}; every state HttpClient carries Bearer auth.",
            options.ServiceTokenFile);
    }
}
