using System.Net;
using Microsoft.Extensions.FileProviders;

namespace JeebGateway.Services.Clients;

/// <summary>Bounded CLI-only proof; intercepted before app/worker construction.</summary>
internal static class DeliveryActivationProbe
{
    internal const string MountedPath = "/run/secrets/delivery_service_token";
    internal const string BaseUrl = "http://192.168.2.20:10055";
    internal const string ReadyUrl = BaseUrl + "/internal/service-auth/ready";

    internal static void ValidateInvocation(string[] args, string? environment, string? tokenPath, string? baseUrl)
    {
        if (args.Length != 2 || args[0] != "--staging-delivery-auth-probe" ||
            !ValidMode(args[1]) || environment != "Production" ||
            tokenPath != MountedPath || baseUrl != BaseUrl)
            throw new InvalidOperationException();
    }

    private static bool ValidMode(string mode) => mode is "credential" or "wire" or "missing" or "invalid" or "duplicate";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            ValidateInvocation(args, Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("DELIVERY_SERVICE_TOKEN_FILE"),
                Environment.GetEnvironmentVariable("Services__Delivery__BaseUrl"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DELIVERY_SERVICE_TOKEN_FILE"] = MountedPath,
                ["Services:Delivery:BaseUrl"] = BaseUrl
            }).Build();
            using var transport = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            await ProbeAsync(args[1], configuration, transport, timeout.Token);
            Console.WriteLine(args[1] == "credential" ? "delivery credential ready" :
                args[1] == "wire" ? "delivery authenticated wire ready" : "delivery unauthorized request rejected");
            return 0;
        }
        catch
        {
            // File/HTTP exceptions can carry private values. Never serialize them.
            Console.Error.WriteLine("Delivery activation probe failed.");
            return 1;
        }
    }

    internal static async Task ProbeAsync(string mode, IConfiguration configuration,
        HttpMessageHandler transport, CancellationToken cancellationToken)
    {
        if (!ValidMode(mode) || configuration["Services:Delivery:BaseUrl"] != BaseUrl)
            throw new InvalidOperationException();
        var environment = new ProbeEnvironment();
        var token = await DeliveryServiceCredentialHandler.ReadTokenAsync(configuration, environment, cancellationToken);
        if (mode == "credential") return;
        if (mode != "wire")
        {
            // Negative controls intentionally bypass the credential-adding
            // handler. Fixed GET only; no caller-supplied destination or value.
            using var negativeClient = new HttpClient(transport, disposeHandler: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReadyUrl);
            if (mode == "invalid") request.Headers.TryAddWithoutValidation(DeliveryServiceCredentialHandler.HeaderName, Guid.NewGuid().ToString("N"));
            if (mode == "duplicate") request.Headers.TryAddWithoutValidation(DeliveryServiceCredentialHandler.HeaderName, new[] { token, token });
            using var negative = await negativeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (negative.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException();
            return;
        }
        using var handler = new DeliveryServiceCredentialHandler(configuration, environment) { InnerHandler = transport };
        using var client = new HttpClient(handler, disposeHandler: false);
        using var response = await client.GetAsync(ReadyUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new InvalidOperationException();
    }

    private sealed class ProbeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = "/app";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
