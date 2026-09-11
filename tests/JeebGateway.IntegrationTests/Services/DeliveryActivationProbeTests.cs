using System.Net;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.Services;

public sealed class DeliveryActivationProbeTests
{
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void ExactDeployedEnvironmentsSupportEveryProofOperation(string environment)
    {
        foreach (var mode in new[] { "credential", "wire", "missing", "invalid", "duplicate" })
            DeliveryActivationProbe.ValidateInvocation(["--staging-delivery-auth-probe", mode],
                environment, DeliveryActivationProbe.MountedPath, DeliveryActivationProbe.BaseUrl);
    }

    [Theory]
    [InlineData("credential", 0)]
    [InlineData("wire", 1)]
    public async Task ProofUsesMountedCredentialAndOnlyFixedReadiness(string mode, int requests)
    {
        var path = Path.GetTempFileName();
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(path, token + "\n");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DELIVERY_SERVICE_TOKEN_FILE"] = path, ["Services:Delivery:BaseUrl"] = DeliveryActivationProbe.BaseUrl }).Build();
            using var capture = new Capture(token);
            await DeliveryActivationProbe.ProbeAsync(mode, config, capture, CancellationToken.None);
            capture.Calls.Should().Be(requests);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFileNeverSendsOwnerRequest()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["DELIVERY_SERVICE_TOKEN_FILE"] = "/nonexistent-delivery-fixture", ["Services:Delivery:BaseUrl"] = DeliveryActivationProbe.BaseUrl }).Build();
        using var capture = new Capture("unused");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeliveryActivationProbe.ProbeAsync(
            "wire", config, capture, CancellationToken.None));
        capture.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("bad", "Production", "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", "Development", "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", "Testing", "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", "Preview", "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", "staging", "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", "", "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", null, "/run/secrets/delivery_service_token", "http://192.168.2.20:10055")]
    [InlineData("wire", "Production", "/tmp/other", "http://192.168.2.20:10055")]
    [InlineData("wire", "Production", "/run/secrets/delivery_service_token", "http://elsewhere")]
    [InlineData("wire", "Staging", "/tmp/other", "http://192.168.2.20:10055")]
    [InlineData("wire", "Staging", "/run/secrets/delivery_service_token", "http://elsewhere")]
    public void InvalidInvocationIsRejected(string mode, string? environment, string path, string url)
    {
        Assert.Throws<InvalidOperationException>(() => DeliveryActivationProbe.ValidateInvocation(
            ["--staging-delivery-auth-probe", mode], environment, path, url));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(301)]
    [InlineData(401)]
    public async Task Non204IsNotAuthenticatedReadiness(int status)
    {
        var path = Path.GetTempFileName();
        var token = Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(path, token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DELIVERY_SERVICE_TOKEN_FILE"] = path, ["Services:Delivery:BaseUrl"] = DeliveryActivationProbe.BaseUrl }).Build();
            using var capture = new Capture(token, (HttpStatusCode)status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DeliveryActivationProbe.ProbeAsync("wire", config, capture, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("invalid\nembedded\nnewlines")]
    public async Task MalformedFileCannotSendRequest(string value)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, value);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DELIVERY_SERVICE_TOKEN_FILE"] = path, ["Services:Delivery:BaseUrl"] = DeliveryActivationProbe.BaseUrl }).Build();
            using var capture = new Capture("unused");
            await Assert.ThrowsAsync<InvalidOperationException>(() => DeliveryActivationProbe.ProbeAsync("wire", config, capture, CancellationToken.None));
            capture.Calls.Should().Be(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledTransportCannotProveReadiness()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Guid.NewGuid().ToString("N"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DELIVERY_SERVICE_TOKEN_FILE"] = path, ["Services:Delivery:BaseUrl"] = DeliveryActivationProbe.BaseUrl }).Build();
            using var transport = new CancelledTransport();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeliveryActivationProbe.ProbeAsync(
                "wire", config, transport, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    private sealed class CancelledTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true));
    }

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("invalid", 401)]
    [InlineData("duplicate", 401)]
    [InlineData("missing", 204)]
    [InlineData("invalid", 204)]
    [InlineData("duplicate", 204)]
    public async Task NegativeControlsRequire401(string mode, int status)
    {
        var path = Path.GetTempFileName();
        var token = Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(path, token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["DELIVERY_SERVICE_TOKEN_FILE"] = path, ["Services:Delivery:BaseUrl"] = DeliveryActivationProbe.BaseUrl }).Build();
            using var transport = new NegativeCapture(mode, token, (HttpStatusCode)status);
            var action = () => DeliveryActivationProbe.ProbeAsync(mode, config, transport, CancellationToken.None);
            if (status == 401) await action();
            else await Assert.ThrowsAsync<InvalidOperationException>(action);
        }
        finally { File.Delete(path); }
    }

    private sealed class NegativeCapture(string mode, string token, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.Should().Be(DeliveryActivationProbe.ReadyUrl);
            if (mode == "missing") request.Headers.Contains(DeliveryServiceCredentialHandler.HeaderName).Should().BeFalse();
            else if (mode == "invalid") request.Headers.GetValues(DeliveryServiceCredentialHandler.HeaderName).Single().Should().NotBe(token);
            else request.Headers.GetValues(DeliveryServiceCredentialHandler.HeaderName).Should().Equal(token, token);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class Capture(string token, HttpStatusCode status = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.Should().Be(DeliveryActivationProbe.ReadyUrl);
            request.Headers.GetValues("X-Delivery-Service-Token").Should().Equal(token);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
