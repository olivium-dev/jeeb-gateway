using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Realtime;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Realtime;

/// <summary>RTC-rename G0 pins: default = today's live names byte-identically; a flip
/// renames coherently; old URLs stay deprecated aliases; unknown tenants are no route.</summary>
public class RealtimeTenantPrefixTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _bare;

    public RealtimeTenantPrefixTests(WebApplicationFactory<Program> factory)
    {
        _bare = factory;
    }

    private static RealtimeTopicNames Names(string prefix = "jeeb")
        => new(Options.Create(new RealtimeGuardianOptions { TenantPrefix = prefix }));

    // 1. Default names are byte-identical to what live phones use today.

    [Fact]
    public void Default_Prefix_Builds_Todays_Live_Names_Byte_Identically()
    {
        var names = Names();
        names.TenantPrefix.Should().Be("jeeb");
        names.ChatTopic.Should().Be("jeeb:chat");
        names.DeliveryTopicPrefix.Should().Be("jeeb:delivery:");
        names.ConversationChannelPrefix.Should().Be("jeeb_conversation:");
        names.DeliveryTopicFor("d-1").Should().Be("jeeb:delivery:d-1");
        names.ConversationChannelFor("c-1").Should().Be("jeeb_conversation:c-1");
    }

    // 2. A configured prefix renames coherently; unsafe prefixes fail closed.

    [Fact]
    public void Configured_Prefix_Renames_Every_Name_Coherently()
    {
        var names = Names("acme");
        names.ChatTopic.Should().Be("acme:chat");
        names.DeliveryTopicFor("d-1").Should().Be("acme:delivery:d-1");
        names.ConversationChannelFor("c-1").Should().Be("acme_conversation:c-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a b")]
    public void Unsafe_Prefix_Refuses_To_Construct(string prefix)
    {
        // ':' / '*' in the prefix would widen or escape the realtime ACL namespace.
        var act = () => Names(prefix);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Unsafe_Delivery_Id_Still_Yields_No_Topic()
    {
        Names().DeliveryTopicFor("a:b").Should().BeNull();
        Names().DeliveryTopicFor("a*b").Should().BeNull();
        Names().DeliveryTopicFor(null).Should().BeNull();
    }

    [Fact]
    public void Accepted_Tenants_Are_The_Configured_Prefix_Plus_The_Legacy_Alias()
    {
        Names().IsAcceptedTenant("jeeb").Should().BeTrue();
        Names().IsAcceptedTenant("acme").Should().BeFalse();

        var flipped = Names("acme");
        flipped.IsAcceptedTenant("acme").Should().BeTrue();
        flipped.IsAcceptedTenant("jeeb").Should().BeTrue("old URLs stay deprecated aliases");
        flipped.IsAcceptedTenant("other").Should().BeFalse();
        flipped.IsAcceptedTenant(null).Should().BeFalse();
    }

    [Fact]
    public void Route_Constraint_Enforces_The_Accepted_Tenant_Set()
    {
        var constraint = new RealtimeTenantRouteConstraint();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(Names()).BuildServiceProvider(),
        };

        bool Match(object? tenant) => constraint.Match(
            ctx, route: null, "tenant",
            new RouteValueDictionary { ["tenant"] = tenant }, RouteDirection.IncomingRequest);

        Match("jeeb").Should().BeTrue();
        Match("acme").Should().BeFalse();
        Match(null).Should().BeFalse();
        constraint.Match(ctx, null, "tenant",
            new RouteValueDictionary(), RouteDirection.IncomingRequest).Should().BeFalse();
    }

    // 3. Route matching. Anonymous is 401 on ANY path (ADR-004 FallbackPolicy) so
    //    real sessions are the oracle: matched route = flags-off 503, rejected = 404.

    [Fact]
    public async Task Anonymous_Requests_Draw_The_Same_401_Challenge_As_Before_The_Rename()
    {
        var http = _bare.CreateClient();

        (await http.GetAsync("/v1/realtime/jeeb:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the live literal URL still routes");
        (await http.GetAsync("/v1/realtime/jeeb:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the live literal URL still routes");
        (await http.GetAsync("/v1/realtime/acme:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "FallbackPolicy challenged this pre-rename too");
    }

    [Fact]
    public async Task Default_Config_Routes_The_Live_Tenant_And_404s_Unknown_Tenants()
    {
        using var factory = OtpFactory(prefix: null);
        var http = await SessionAsync(factory);

        (await http.GetAsync("/v1/realtime/jeeb:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the live URL routes to the flags-off gate");
        (await http.GetAsync("/v1/realtime/acme:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "an unconfigured tenant is not a route");
    }

    [Fact]
    public async Task Flipped_Prefix_Routes_Both_New_And_Legacy_Urls_And_Still_404s_Others()
    {
        using var factory = OtpFactory(prefix: "acme");
        var http = await SessionAsync(factory);

        (await http.GetAsync("/v1/realtime/acme:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the new tenant URL must route");
        (await http.GetAsync("/v1/realtime/jeeb:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the legacy URL stays a deprecated alias");
        (await http.GetAsync("/v1/realtime/other:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Same no-op-OTP session arrangement CourierPositionRealtimeTests uses: the
    // descriptor gates are [Authorize]d and need a real bearer with sub == userId.
    private WebApplicationFactory<Program> OtpFactory(string? prefix)
        => _bare.WithWebHostBuilder(builder =>
        {
            if (prefix is not null)
            {
                builder.UseSetting("Services:Realtime:TenantPrefix", prefix);
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubServiceOtpClient());
                services.Configure<JeebGateway.Services.UpstreamFeatureFlags>(f => f.Otp = true);
                services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test-app";
                    o.TtlSeconds = 300;
                });
            });
        });

    private static async Task<HttpClient> SessionAsync(WebApplicationFactory<Program> factory)
    {
        var bootstrap = factory.CreateClient();
        var phone = $"+9665{Random.Shared.NextInt64(10_000_000, 99_999_999)}";
        var resp = await bootstrap.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var token = json.GetProperty("accessToken").GetString()!;

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
