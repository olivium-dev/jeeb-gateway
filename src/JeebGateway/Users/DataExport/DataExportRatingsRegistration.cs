using JeebGateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Composition of the GDPR-export ratings provider. Lives here rather than inline in
/// Program.cs so the binding itself — the thing that was missing (codex ledger §8-19) —
/// is assertable without booting a host.
/// </summary>
public static class DataExportRatingsRegistration
{
    public static IServiceCollection AddDataExportRatingsProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration
            .GetSection(FeedbackRatingExportOptions.SectionName)
            .Get<FeedbackRatingExportOptions>() ?? new FeedbackRatingExportOptions();

        services.AddSingleton(options);
        services.AddSingleton<InMemoryDataExportRatingsProvider>();

        if (!options.IsConfigured)
        {
            // No mounted credential: the pipeline stays exactly as it was (dev/CI).
            services.AddSingleton<IDataExportRatingsProvider>(
                sp => sp.GetRequiredService<InMemoryDataExportRatingsProvider>());
            return services;
        }

        var baseUrl = configuration["FeedbackServiceApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"{FeedbackRatingExportOptions.SectionName}:ServiceTokenFile is configured, which makes " +
                "feedback-service the source of the GDPR export's ratings section, but " +
                "FeedbackServiceApi:BaseUrl is unset. Set FeedbackServiceApi__BaseUrl or clear the token file.");
        }

        services.AddTransient(_ => new FeedbackExportCredentialHandler(options));
        ServiceClientExtensions.AttachResilienceOnly(services
            .AddHttpClient(FeedbackServiceDataExportRatingsProvider.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .AddHttpMessageHandler<FeedbackExportCredentialHandler>());
        services.AddSingleton<IDataExportRatingsProvider, FeedbackServiceDataExportRatingsProvider>();

        return services;
    }
}
