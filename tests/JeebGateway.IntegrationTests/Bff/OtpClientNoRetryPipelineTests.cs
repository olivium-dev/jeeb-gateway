using System.Net;
using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Bff;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// Regression guard for the real NSwag OTP typed-client registration. Both OTP
/// operations are non-idempotent POSTs, so transport faults and transient responses
/// must reach the gateway after one dispatch instead of being replayed automatically.
/// </summary>
public sealed class OtpClientNoRetryPipelineTests
{
    [Theory]
    [InlineData(OtpOperation.Send)]
    [InlineData(OtpOperation.Validate)]
    public async Task Otp_Post_Dispatches_Once_When_Upstream_Returns_503(OtpOperation operation)
    {
        var terminal = new CountingFailureHandler(FailureMode.ServiceUnavailable);
        using var provider = BuildGatewayServiceProvider(terminal);
        var client = provider.GetRequiredService<IServiceOTPClient>();

        var act = () => InvokeAsync(client, operation);

        var exception = await act.Should().ThrowAsync<ApiException>();
        exception.Which.StatusCode.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        terminal.DispatchCount.Should().Be(1,
            $"OTP {operation} must not replay a non-idempotent POST after HTTP 503");
    }

    [Theory]
    [InlineData(OtpOperation.Send)]
    [InlineData(OtpOperation.Validate)]
    public async Task Otp_Post_Dispatches_Once_When_Transport_Throws(OtpOperation operation)
    {
        var terminal = new CountingFailureHandler(FailureMode.TransportException);
        using var provider = BuildGatewayServiceProvider(terminal);
        var client = provider.GetRequiredService<IServiceOTPClient>();

        var act = () => InvokeAsync(client, operation);

        await act.Should().ThrowAsync<HttpRequestException>();
        terminal.DispatchCount.Should().Be(1,
            $"OTP {operation} must not replay a non-idempotent POST after a transport fault");
    }

    [Fact]
    public void Otp_Breaker_Counts_Upstream_Failures_But_Not_Expected_Throttle()
    {
        using var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var timedOut = new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        using var badRequest = new HttpResponseMessage(HttpStatusCode.BadRequest);

        ServiceClientExtensions.ShouldBreakOtp(exception: null, throttled).Should().BeFalse();
        ServiceClientExtensions.ShouldBreakOtp(exception: null, unavailable).Should().BeTrue();
        ServiceClientExtensions.ShouldBreakOtp(exception: null, timedOut).Should().BeTrue();
        ServiceClientExtensions.ShouldBreakOtp(exception: null, badRequest).Should().BeFalse();
        ServiceClientExtensions.ShouldBreakOtp(new HttpRequestException("network fault"), response: null)
            .Should().BeTrue();
    }

    private static Task InvokeAsync(IServiceOTPClient client, OtpOperation operation) =>
        operation switch
        {
            OtpOperation.Send => client.SendOTPAsync(new SendOTPRequestUserID
            {
                PhoneNumber = "+15550109999",
                ApplicationId = "0d51afe1-499f-4a29-a55a-36d2dd223b05",
            }),
            OtpOperation.Validate => client.ValidateOTPAsync(new ValidateOTPRequestModel
            {
                PhoneNumber = "+15550109999",
                Otp = "1234",
                ApplicationId = "0d51afe1-499f-4a29-a55a-36d2dd223b05",
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static ServiceProvider BuildGatewayServiceProvider(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostEnvironment>(new PipelineHostEnvironment());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:Auth"] = "http://auth.test",
                ["Services:Delivery"] = "http://delivery.test",
                ["Services:Geolocation"] = "http://geo.test",
                ["Services:Cdn:BaseUrl"] = "http://cdn.test",
                ["Services:ServiceOTP:BaseUrl"] = "http://otp.test",
                ["Services:FormBuilder:BaseUrl"] = "http://form-builder.test",
                ["ServiceAuth:Caller"] = "jeeb-gateway",
                ["ServiceAuth:SigningKey"] = "integration-test-signing-key-32-chars-or-longer",
                ["ServiceAuth:Enabled"] = "true",
                ["DELIVERY_SERVICE_TOKEN"] = new string('t', 48),
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.Configure<ServiceAuthOptions>(config.GetSection(ServiceAuthOptions.SectionName));
        services.AddDownstreamClients(config);

        // AddHttpClient<TClient>() uses the interface name as its factory key. Add
        // the terminal override after the production registration so every request
        // still traverses the real auth and resilience handlers first.
        services.AddHttpClient(nameof(IServiceOTPClient))
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        return services.BuildServiceProvider();
    }

    public enum OtpOperation
    {
        Send,
        Validate,
    }

    private enum FailureMode
    {
        ServiceUnavailable,
        TransportException,
    }

    private sealed class CountingFailureHandler(FailureMode failureMode) : HttpMessageHandler
    {
        private int _dispatchCount;

        public int DispatchCount => Volatile.Read(ref _dispatchCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _dispatchCount);

            if (failureMode == FailureMode.TransportException)
                throw new HttpRequestException("simulated transport failure");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}"),
                RequestMessage = request,
            });
        }
    }

    private sealed class PipelineHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "JeebGateway.IntegrationTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
