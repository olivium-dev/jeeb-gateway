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
            // Geolocation, Delivery missing. Notification moved to the top-level
            // ServiceNotificationClient key and PushNotification moved to the
            // top-level PushNotificationServiceApi key, so neither is a required
            // Services:* key any more.
        });

        var opts = Options.Create(new DownstreamServicesOptions
        {
            RequiredInProduction = true,
        });

        var validator = new BffStartupValidator(opts, config, new HostEnv("Production"));

        var act = () => validator.Validate();

        // CONFIG-KEY ALIGNMENT: UserManagement is validated against the canonical
        // top-level UserManagementServiceApi:BaseUrl key (the one the live
        // ServiceUserManagementClient in Program.cs + the readiness probe dial),
        // NOT a phantom Services:UserManagement:BaseUrl. Geolocation/Delivery
        // remain nested Services:* downstreams.
        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().ContainAll(
                "UserManagementServiceApi:BaseUrl",
                "Services:Geolocation:BaseUrl",
                "Services:Delivery:BaseUrl",
                "environment 'Production'");

        // It must NOT name the old phantom key (the drift that forced the
        // dual-key env workaround).
        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().NotContain("Services:UserManagement:BaseUrl");

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
            // CONFIG-KEY ALIGNMENT: UserManagement validates the canonical
            // top-level key, mirroring appsettings + Program.cs.
            ["UserManagementServiceApi:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test",
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
            // Bare form of the canonical UserManagement key (key minus :BaseUrl).
            ["UserManagementServiceApi"] = "http://user-management.test",
            ["Services:Matching"] = "http://matching.test",
            ["Services:Geolocation"] = "http://geo.test",
            ["Services:Delivery"] = "http://delivery.test",
        });

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Production"));

        validator.Validate(); // does not throw
    }

    [Fact]
    public void UserManagement_Resolves_The_Canonical_Top_Level_Key()
    {
        // Regression for the config-key-drift fix: with ONLY the canonical
        // UserManagementServiceApi:BaseUrl set (and the phantom
        // Services:UserManagement:BaseUrl absent), UserManagement counts as
        // configured. The other nested Services:* downstreams are supplied so the
        // validator's only verdict under test is the UserManagement key resolution.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Services:Auth:BaseUrl"] = "http://auth.test",
            ["UserManagementServiceApi:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test",
            ["Services:Delivery:BaseUrl"] = "http://delivery.test",
        });

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Production"));

        validator.Validate(); // does not throw — phantom Services:UserManagement key not needed
    }

    [Fact]
    public void Throws_When_Otp_Enabled_But_ApplicationId_Empty()
    {
        // JEBV4 OTP-502 regression: the exact production misconfig that caused the
        // 2026-07-12 phone-OTP login outage — OTP enabled, ApplicationId not injected.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            // All required downstream BaseUrls present, so the ONLY verdict under
            // test is the OTP ApplicationId guard.
            ["Services:Auth:BaseUrl"] = "http://auth.test",
            ["UserManagementServiceApi:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test",
            ["Services:Delivery:BaseUrl"] = "http://delivery.test",
            ["FeatureFlags:UseUpstream:Otp"] = "true",
            ["Auth:Otp:ApplicationId"] = "", // empty placeholder never overridden at deploy
        });

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Production"));

        var act = () => validator.Validate();

        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().ContainAll("Auth:Otp:ApplicationId", "Auth__Otp__ApplicationId");
    }

    [Fact]
    public void Passes_When_Otp_Enabled_And_ApplicationId_Set()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Services:Auth:BaseUrl"] = "http://auth.test",
            ["UserManagementServiceApi:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test",
            ["Services:Delivery:BaseUrl"] = "http://delivery.test",
            ["FeatureFlags:UseUpstream:Otp"] = "true",
            ["Auth:Otp:ApplicationId"] = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46",
        });

        var validator = new BffStartupValidator(
            Options.Create(new DownstreamServicesOptions()),
            config,
            new HostEnv("Production"));

        validator.Validate(); // does not throw
    }

    [Fact]
    public void Skips_Otp_Guard_When_Otp_Disabled()
    {
        // OTP off → empty ApplicationId is harmless (no /v1/auth/otp/* traffic).
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Services:Auth:BaseUrl"] = "http://auth.test",
            ["UserManagementServiceApi:BaseUrl"] = "http://user-management.test",
            ["Services:Matching:BaseUrl"] = "http://matching.test",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test",
            ["Services:Delivery:BaseUrl"] = "http://delivery.test",
            ["FeatureFlags:UseUpstream:Otp"] = "false",
            ["Auth:Otp:ApplicationId"] = "",
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
