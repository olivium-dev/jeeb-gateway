using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Bff;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

public sealed class ServiceAuthOptionsTests
{
    private const string InlineKey = "inline-signing-key-32-characters-long";
    private const string FileKey = "mounted-signing-key-32-characters-long";

    [Fact]
    public void Mounted_Secret_File_Takes_Precedence_And_Is_Trimmed()
    {
        var keyFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(keyFile, $"{FileKey}\n");
            var options = ResolveOptions(new Dictionary<string, string?>
            {
                ["ServiceAuth:Enabled"] = "true",
                ["ServiceAuth:SigningKey"] = InlineKey,
                ["ServiceAuth:SigningKeyFile"] = keyFile,
            });

            options.SigningKey.Should().Be(FileKey);
            options.SigningKeyFile.Should().Be(keyFile);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void Inline_Key_Remains_Compatible_For_Native_Deployments()
    {
        var options = ResolveOptions(new Dictionary<string, string?>
        {
            ["ServiceAuth:Enabled"] = "true",
            ["ServiceAuth:SigningKey"] = InlineKey,
        });

        options.SigningKey.Should().Be(InlineKey);
        options.SigningKeyFile.Should().BeEmpty();
    }

    [Fact]
    public void Enabled_Mode_Rejects_A_Short_Resolved_Key()
    {
        var action = () => ResolveOptions(new Dictionary<string, string?>
        {
            ["ServiceAuth:Enabled"] = "true",
            ["ServiceAuth:SigningKey"] = "too-short",
        });

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*at least 32 characters*");
    }

    private static ServiceAuthOptions ResolveOptions(
        Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBffAggregation(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ServiceAuthOptions>>().Value;
    }
}
