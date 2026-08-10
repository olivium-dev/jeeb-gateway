using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Services;

public sealed class DeliveryServiceCredentialHandlerTests
{
    [Fact]
    public async Task Production_reads_mounted_file_and_sets_exact_header()
    {
        var path = Path.GetTempFileName();
        try
        {
            var token = new string('d', 48);
            await File.WriteAllTextAsync(path, token + "\n");
            var terminal = new CaptureHandler();
            using var client = new HttpClient(new DeliveryServiceCredentialHandler(
                Configuration(("DELIVERY_SERVICE_TOKEN_FILE", path)),
                new TestEnvironment("Production"))
            {
                InnerHandler = terminal,
            });

            using var response = await client.GetAsync("http://delivery.test/api/v1/tiers");

            response.IsSuccessStatusCode.Should().BeTrue();
            terminal.Headers.Should().ContainKey("X-Delivery-Service-Token")
                .WhoseValue.Should().Equal(token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Production_rejects_direct_value_without_mounted_file()
    {
        var handler = new DeliveryServiceCredentialHandler(
            Configuration(("DELIVERY_SERVICE_TOKEN", new string('d', 48))),
            new TestEnvironment("Production"))
        {
            InnerHandler = new CaptureHandler(),
        };
        using var client = new HttpClient(handler);

        var act = () => client.GetAsync("http://delivery.test/api/v1/tiers");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DELIVERY_SERVICE_TOKEN_FILE*");
    }

    [Fact]
    public async Task Testing_can_use_explicit_direct_fixture_token()
    {
        var token = new string('t', 48);
        var terminal = new CaptureHandler();
        using var client = new HttpClient(new DeliveryServiceCredentialHandler(
            Configuration(("DELIVERY_SERVICE_TOKEN", token)),
            new TestEnvironment("Testing"))
        {
            InnerHandler = terminal,
        });

        await client.GetAsync("http://delivery.test/api/v1/tiers");

        terminal.Headers["X-Delivery-Service-Token"].Should().Equal(token);
    }

    [Theory]
    [InlineData("ddddddddddddddddddddddddddddddd,")]
    [InlineData("ddddddddddddddddddddddddddddddd ")]
    [InlineData("ddddddddddddddddddddddddddddddd\t")]
    [InlineData("dddddddddddddddddddddddddddddddé")]
    public async Task Direct_fixture_rejects_non_header_safe_tokens(string token)
    {
        using var client = new HttpClient(new DeliveryServiceCredentialHandler(
            Configuration(("DELIVERY_SERVICE_TOKEN", token)),
            new TestEnvironment("Testing"))
        {
            InnerHandler = new CaptureHandler(),
        });

        var act = () => client.GetAsync("http://delivery.test/api/v1/tiers");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid*");
    }

    [Theory]
    [InlineData(" dddddddddddddddddddddddddddddddd")]
    [InlineData("dddddddddddddddddddddddddddddddd ")]
    [InlineData("dddddddddddddddddddddddddddddddd\n\n")]
    [InlineData("ddddddddddddddddddddddddddddddd,")]
    public async Task Mounted_file_rejects_trimming_or_header_folding_ambiguity(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            using var client = new HttpClient(new DeliveryServiceCredentialHandler(
                Configuration(("DELIVERY_SERVICE_TOKEN_FILE", path)),
                new TestEnvironment("Production"))
            {
                InnerHandler = new CaptureHandler(),
            });

            var act = () => client.GetAsync("http://delivery.test/api/v1/tiers");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*invalid credential*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Mounted_file_accepts_exactly_one_terminal_crlf()
    {
        var path = Path.GetTempFileName();
        try
        {
            var token = new string('d', 32);
            await File.WriteAllTextAsync(path, token + "\r\n");
            var terminal = new CaptureHandler();
            using var client = new HttpClient(new DeliveryServiceCredentialHandler(
                Configuration(("DELIVERY_SERVICE_TOKEN_FILE", path)),
                new TestEnvironment("Production"))
            {
                InnerHandler = terminal,
            });

            await client.GetAsync("http://delivery.test/api/v1/tiers");

            terminal.Headers["X-Delivery-Service-Token"].Should().Equal(token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
                Headers[header.Key] = header.Value.ToArray();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "JeebGateway.IntegrationTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
