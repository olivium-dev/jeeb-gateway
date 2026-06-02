using FluentAssertions;
using JeebGateway.Services.Bff;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC1 — BffStartupValidator.
///
/// Asserts:
///   * Production env + missing required keys → throws StartupConfigurationException
///     listing EVERY missing key in the message
///   * Development env → skips validation (so local dev does not need every URL)
///   * Testing env → skips validation (so WebApplicationFactory boots)
///   * RequiredInProduction=false → skips validation regardless of env
///   * All required keys present → passes silently
///   * Bare URL form (Services:Foo) is accepted as equivalent to Services:Foo:BaseUrl
/// </summary>
public class BffStartupValidatorTests
{
    [Fact]
    public void Throws_With_Every_Missing_Key_Named_In_Production()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Services:Auth:BaseUrl"] = "http://auth.test",
            // Chat, UserManagement missing
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            // Notification, Geolocation, PushNotification, Delivery missing
        });

        var opts = Options.Create(new DownstreamServicesOptions
        {
            RequiredInProduction = true,
        });

        var validator = new BffStartupValidator(opts, config, new HostEnv("Production"));

        var act = () => validator.Validate();

        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().ContainAll(
                "Services:Chat:BaseUrl",
                "Services:UserManagement:BaseUrl",
                "Services:Notification:BaseUrl",
                "Services:Geolocation:BaseUrl",
                "Services:PushNotification:BaseUrl",
                "Services:Delivery:BaseUrl",
                "environment 'Production'");
    }

    [Fact]
    public void Skips_Validation_In_Development()
    {
        var config = BuildConfig(new Dictionary<string, string?>()); // nothing configured

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Development"));

        validator.Validate(); // does not throw
    }

    [Fact]
    public void Skips_Validation_In_Testing_Environment()
    {
        var config = BuildConfig(new Dictionary<string, string?>()); // nothing configured

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Testing"));

        validator.Validate(); // does not throw
    }

    [Fact]
    public void Skips_When_RequiredInProduction_Is_False()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions { RequiredInProduction = false }),
            config,
            new HostEnv("Production"));

        validator.Validate(); // does not throw
    }

    [Fact]
    public void Passes_When_All_Required_Keys_Configured()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Services:Auth:BaseUrl"] = "http://auth.test",
            ["Services:Chat:BaseUrl"] = "http://chat.test",
            ["Services:UserManagement:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            ["Services:Notification:BaseUrl"] = "http://notification.test",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test",
            ["Services:PushNotification:BaseUrl"] = "http://push.test",
            ["Services:Delivery:BaseUrl"] = "http://delivery.test",
        });

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Production"));

        validator.Validate(); // does not throw
    }

    [Fact]
    public void Accepts_Bare_Url_Form_As_Configured()
    {
        // appsettings.json today uses the bare form for Auth/Delivery/Matching/Geolocation.
        // The validator must accept that as well as the nested :BaseUrl form.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Services:Auth"] = "http://auth.test",
            ["Services:Chat"] = "http://chat.test",
            ["Services:UserManagement"] = "http://user-management.test",
            ["Services:Matching"] = "http://matching.test",
            ["Services:Notification"] = "http://notification.test",
            ["Services:Geolocation"] = "http://geo.test",
            ["Services:PushNotification"] = "http://push.test",
            ["Services:Delivery"] = "http://delivery.test",
        });

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Production"));

        validator.Validate(); // does not throw
    }

    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class HostEnv : IHostEnvironment
    {
        public HostEnv(string env) => EnvironmentName = env;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "JeebGateway.Tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
