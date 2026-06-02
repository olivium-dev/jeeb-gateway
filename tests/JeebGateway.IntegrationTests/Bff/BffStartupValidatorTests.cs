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
            // UserManagement missing (Chat is no longer a required
            // Services:* key — it moved to the top-level ChatServiceApi key;
            // Wallet also moved to the top-level WalletServiceApi key)
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            // Geolocation, PushNotification, Delivery missing (Notification moved
            // to the top-level ServiceNotificationClient key, so it is no longer
            // a required Services:* key)
        });

        var opts = Options.Create(new DownstreamServicesOptions
        {
            RequiredInProduction = true,
        });

        var validator = new BffStartupValidator(opts, config, new HostEnv("Production"));

        var act = () => validator.Validate();

        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().ContainAll(
                "Services:UserManagement:BaseUrl",
                "Services:Geolocation:BaseUrl",
                "Services:PushNotification:BaseUrl",
                "Services:Delivery:BaseUrl",
                "environment 'Production'");

        // Chat is intentionally NOT a required Services:* key anymore (salehly
        // mirror moved it to the top-level ChatServiceApi key), so even with the
        // Services:Chat URL absent the validator must not name it.
        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().NotContain("Services:Chat");
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
            ["Services:UserManagement:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
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
            ["Services:UserManagement"] = "http://user-management.test",
            ["Services:Matching"] = "http://matching.test",
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
