using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.Services;

public sealed class DeliveryClientCredentialWiringTests
{
    public static IEnumerable<object[]> Clients() => new[] {
        "delivery", "IDeliveryServiceClient", "ICaseDeliveryClient", "IRequestsOwnerClient",
        "EscalationMirror", "AvailabilityMirror", "TiersUpstream", "admin-deliveries-owner",
    }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task ActualRegisteredClientSendsMountedCredentialAndRetainsIndependentImportBearer(string name)
    {
        var path = Path.GetTempFileName();
        const string token = "delivery-service-wiring-test-token-48-characters-only";
        const string import = "independent-import-test-token-at-least-32-bytes";
        try
        {
            await File.WriteAllTextAsync(path, token + "\n");
            var capture = new Capture();
            using var factory = Factory(name, path, import, capture);
            using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://delivery.test/internal/service-auth/ready");
            request.Headers.TryAddWithoutValidation("X-Delivery-Service-Token", "attacker-supplied");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            capture.Calls.Should().Be(1);
            capture.Tokens.Should().Equal(token);
            if (name == "IRequestsOwnerClient") capture.Authorization.Should().Be("Bearer " + import);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task MissingMountedCredentialFailsBeforeAnyOwnerRequest(string name)
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-delivery-token-" + Guid.NewGuid());
        var capture = new Capture();
        using var factory = Factory(name, missing, new string('i', 48), capture);
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://delivery.test/internal/service-auth/ready"));
        capture.Calls.Should().Be(0);
    }

    private static WebApplicationFactory<Program> Factory(string name, string path, string import, Capture capture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("FeatureFlags:TiersMode", "upstream-authority");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Services:Delivery:BaseUrl"] = "http://delivery.test/",
                ["FeatureFlags:TiersMode"] = "upstream-authority",
                ["DELIVERY_SERVICE_TOKEN_FILE"] = path,
                ["DELIVERY_IMPORT_TOKEN"] = import,
            }));
            builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(name,
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = capture)));
        });

    private sealed class Capture : HttpMessageHandler
    {
        public int Calls;
        public string[] Tokens = [];
        public string? Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/internal/service-auth/ready")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            Calls++;
            Tokens = request.Headers.TryGetValues("X-Delivery-Service-Token", out var values) ? values.ToArray() : [];
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
