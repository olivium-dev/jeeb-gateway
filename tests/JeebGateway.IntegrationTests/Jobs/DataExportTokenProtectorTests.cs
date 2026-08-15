using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class DataExportTokenProtectorTests
{
    [Fact]
    public void Create_Is_Deterministic_And_Validate_Reconstructs_Only_The_Hashed_Capability()
    {
        using var secret = TempSecret.Create(
            "data-export-hmac-key-0123456789abcdef0123456789abcdef");
        var protector = Protector(secret.Path);
        var workId = Guid.NewGuid();

        var first = protector.Create(workId);
        var replay = protector.Create(workId);

        replay.Should().Be(first);
        first.Token.Should().StartWith($"v1.{workId:N}.");
        first.TokenHash.Should().Be(
            "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(first.Token)))
                .ToLowerInvariant());
        protector.TryValidate(first.Token, out var validated).Should().BeTrue();
        validated.Should().Be(first);
    }

    [Fact]
    public void Tampered_Or_Malformed_Tokens_Are_Rejected()
    {
        using var secret = TempSecret.Create(
            "data-export-hmac-key-0123456789abcdef0123456789abcdef");
        var protector = Protector(secret.Path);
        var token = protector.Create(Guid.NewGuid()).Token;
        var tampered = token[..^1] + (token[^1] == 'A' ? "B" : "A");

        protector.TryValidate(tampered, out _).Should().BeFalse();
        protector.TryValidate("v2.not-a-guid.signature", out _).Should().BeFalse();
        protector.TryValidate("v1.00000000000000000000000000000000.bad*base64", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Missing_Mounted_Key_Fails_Closed()
    {
        var protector = Protector(string.Empty);

        var act = () => protector.Create(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DATA_EXPORT_TOKEN_SIGNING_KEY_FILE*");
    }

    private static DataExportTokenProtector Protector(string keyFile)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataExportTokenProtector.SigningKeyFileKey] = keyFile
            })
            .Build();
        return new DataExportTokenProtector(configuration);
    }

    private sealed class TempSecret : IDisposable
    {
        private TempSecret(string path) => Path = path;
        public string Path { get; }

        public static TempSecret Create(string value)
        {
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllText(path, value);
            return new TempSecret(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
