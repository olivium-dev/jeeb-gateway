using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using JeebGateway.StateService;
using Xunit;

namespace JeebGateway.IntegrationTests.StateService;

public sealed class StateServiceCredentialHandlerTests
{
    [Fact]
    public async Task Sends_file_backed_bearer_without_persisting_it_in_options()
    {
        using var token = TokenFile("state-service-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var capture = new CaptureHandler();
        using var client = Client(token.Path, capture);

        using var response = await client.GetAsync("https://state.test/work-items/id");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capture.Authorization.Should().Be(
            "Bearer state-service-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        capture.Uri.Should().Be("https://state.test/work-items/id");
    }

    [Fact]
    public async Task Reads_each_request_so_atomic_file_rotation_takes_effect_without_restart()
    {
        using var token = TokenFile("state-service-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var capture = new CaptureHandler();
        using var client = Client(token.Path, capture);

        using (await client.GetAsync("https://state.test/work-items/one")) { }
        capture.Authorization.Should().EndWith("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        await File.WriteAllTextAsync(
            token.Path,
            "state-service-token-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n");
        using (await client.GetAsync("https://state.test/work-items/two")) { }

        capture.Authorization.Should().Be(
            "Bearer state-service-token-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-secret")]
    public async Task Missing_or_non_absolute_secret_path_fails_before_network(
        string path)
    {
        var capture = new CaptureHandler();
        using var client = Client(path, capture);

        var act = () => client.GetAsync("https://state.test/work-items");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*absolute mounted-secret path*");
        capture.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_secret_content_fails_closed_and_is_not_echoed_in_error()
    {
        const string invalid = "short-secret";
        using var token = TokenFile(invalid);
        var capture = new CaptureHandler();
        using var client = Client(token.Path, capture);

        var act = () => client.GetAsync("https://state.test/audit-events");

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.ToString().Should().NotContain(invalid);
        capture.Calls.Should().Be(0);
    }

    [Fact]
    public void Production_configuration_commits_no_credential_path_or_value()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "JeebGateway");
        var production = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "appsettings.json"))
            .AddJsonFile(Path.Combine(root, "appsettings.Production.json"))
            .Build();

        // 2026-09-04: the path is no longer committed — /run/secrets exists only under
        // Swarm, and a baked host path is the 608debf class. The deploy supplies it, and
        // credential-state-service-token reports an unsupplied one on /health/ready.
        production["JeebStateService:ServiceTokenFile"].Should().BeNullOrEmpty();
        File.ReadAllText(Path.Combine(root, "appsettings.Production.json"))
            .Should().NotContain("state-service-token-aaaaaaaa");
    }

    private static HttpClient Client(string path, CaptureHandler capture) => new(
        new StateServiceCredentialHandler(new StateServiceOptions
        {
            BaseUrl = "https://state.test/",
            ServiceTokenFile = path
        })
        {
            InnerHandler = capture
        });

    private static TempToken TokenFile(string value)
    {
        var path = System.IO.Path.GetTempFileName();
        File.WriteAllText(path, value);
        return new TempToken(path);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(
                   current.FullName, "src", "JeebGateway", "appsettings.Production.json")))
            current = current.Parent;
        return current?.FullName
               ?? throw new DirectoryNotFoundException("Could not find the gateway repository root.");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public string? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Uri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TempToken(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
