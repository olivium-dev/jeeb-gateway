using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Realtime;

/// <summary>RTC-rename G0 pins: default = today's live names byte-identically; a flip
/// renames coherently; old URLs stay deprecated aliases; unknown tenants still 404.</summary>
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

    // 3. Route matching: old URLs keep working; unknown tenants 404 pre-auth.
    //    401 (bearer challenge) proves the route matched; 404 proves it did not.

    [Fact]
    public async Task Default_Config_Old_Literal_Routes_Match_And_Unknown_Tenants_404()
    {
        var http = _bare.CreateClient();

        (await http.GetAsync("/v1/realtime/jeeb:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the live literal URL must route");
        (await http.GetAsync("/v1/realtime/jeeb:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the live literal URL must route");

        (await http.GetAsync("/v1/realtime/acme:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "an unconfigured tenant is not a route");
        (await http.GetAsync("/v1/realtime/acme:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "an unconfigured tenant is not a route");
    }

    [Fact]
    public async Task Flipped_Prefix_Routes_Both_New_And_Legacy_Urls_And_Still_404s_Others()
    {
        using var flipped = _bare.WithWebHostBuilder(builder =>
            builder.UseSetting("Services:Realtime:TenantPrefix", "acme"));
        var http = flipped.CreateClient();

        (await http.GetAsync("/v1/realtime/acme:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the new tenant URL must route");
        (await http.GetAsync("/v1/realtime/jeeb:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the legacy URL stays a deprecated alias");
        (await http.GetAsync("/v1/realtime/acme:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the new tenant URL must route");
        (await http.GetAsync("/v1/realtime/jeeb:delivery:d-1"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the legacy URL stays a deprecated alias");

        (await http.GetAsync("/v1/realtime/other:chat:conv-1"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
