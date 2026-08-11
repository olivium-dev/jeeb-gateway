using System.Collections.Generic;
using FluentAssertions;
using JeebGateway.StateService;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.StateService;

/// <summary>
/// Every configured key must reach <see cref="StateServiceOptions"/>. The cutover broke because
/// one of them silently did not.
/// </summary>
public sealed class StateServiceOptionsFactoryTests
{
    [Fact]
    public void EveryKeyIsBound()
    {
        var options = StateServiceOptionsFactory.FromConfiguration(Config(new()
        {
            ["JeebStateService:BaseUrl"] = "http://192.168.2.39:10073",
            ["JeebStateService:TimeoutSeconds"] = "9",
            ["JeebStateService:Enabled"] = "true",
            ["JeebStateService:ServiceTokenFile"] = "/home/ec2-user/iter5-native/secrets/state-ownership-token",
        }));

        options.BaseUrl.Should().Be("http://192.168.2.39:10073");
        options.TimeoutSeconds.Should().Be(9);
        options.Enabled.Should().BeTrue();
        options.ServiceTokenFile.Should().Be("/home/ec2-user/iter5-native/secrets/state-ownership-token");
        options.HasServiceCredential.Should().BeTrue();
    }

    [Fact]
    public void TheDefaultsMatchThePreCutoverProductionShape()
    {
        var options = StateServiceOptionsFactory.FromConfiguration(Config(new()
        {
            ["Services:JeebState:BaseUrl"] = "http://legacy:10073",
        }));

        options.BaseUrl.Should().Be("http://legacy:10073", "the legacy key is still honoured");
        options.TimeoutSeconds.Should().Be(5);
        options.Enabled.Should().BeTrue();
        options.HasServiceCredential.Should().BeFalse(
            "no token file means today's unauthenticated live behaviour, unchanged");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTokenFileIsTreatedAsUnset(string value)
    {
        var options = StateServiceOptionsFactory.FromConfiguration(Config(new()
        {
            ["JeebStateService:BaseUrl"] = "http://state",
            ["JeebStateService:ServiceTokenFile"] = value,
        }));

        options.ServiceTokenFile.Should().BeNull();
        options.HasServiceCredential.Should().BeFalse();
    }

    [Fact]
    public void EnabledFalseTurnsTheWholeRewireOff()
    {
        StateServiceOptionsFactory.FromConfiguration(Config(new()
        {
            ["JeebStateService:BaseUrl"] = "http://state",
            ["JeebStateService:Enabled"] = "false",
        })).Enabled.Should().BeFalse();
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
