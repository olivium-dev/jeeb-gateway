using System.Text.Json;
using FluentAssertions;
using JeebGateway.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class JeebFirebaseContractTests
{
    [Fact]
    public void Committed_contract_matches_the_runtime_constants()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile(
            "contracts/jeeb-firebase-v1.json")));
        var root = document.RootElement;

        root.GetProperty("schemaVersion").GetInt32()
            .Should().Be(JeebFirebaseContractOptions.CanonicalSchemaVersion);
        root.GetProperty("projectId").GetString()
            .Should().Be(JeebFirebaseContractOptions.CanonicalProjectId);
        root.GetProperty("projectNumber").GetString()
            .Should().Be(JeebFirebaseContractOptions.CanonicalProjectNumber);
        root.GetProperty("firestoreDatabaseId").GetString()
            .Should().Be(JeebFirebaseContractOptions.CanonicalFirestoreDatabaseId);
        root.GetProperty("chatEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("pushProducer").GetString()
            .Should().Be(JeebFirebaseContractOptions.CanonicalPushProducer);
    }

    [Fact]
    public void Canonical_runtime_options_are_accepted()
    {
        JeebFirebaseContractOptions.IsCanonical(Canonical()).Should().BeTrue();
    }

    [Theory]
    [InlineData("project")]
    [InlineData("database")]
    [InlineData("chat")]
    [InlineData("producer")]
    public void Identity_or_ownership_drift_is_rejected(string mutation)
    {
        var options = mutation switch
        {
            "project" => Canonical(projectId: "another-project"),
            "database" => Canonical(databaseId: "staging"),
            "chat" => Canonical(chatEnabled: false),
            "producer" => Canonical(pushProducer: "jeeb-gateway"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        JeebFirebaseContractOptions.IsCanonical(options).Should().BeFalse();
    }

    [Theory]
    [InlineData("Firestore:DatabaseId")]
    [InlineData("Firebase:FirestoreDatabaseId")]
    [InlineData("Firebase:Chat:FirestoreDatabaseId")]
    public void Named_database_aliases_are_rejected(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "staging" })
            .Build();

        JeebFirebaseContractOptions.HasNoConflictingDatabaseOverride(configuration)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("JeebFirebaseContract:projectId", "another-project", "*JeebFirebaseContract*")]
    [InlineData("Firestore:DatabaseId", "staging", "*Named Firestore database overrides*")]
    [InlineData("FeatureFlags:PushDispatchMode", "upstream-authority", "*PushDispatchMode*")]
    public void Runtime_drift_refuses_to_boot(string key, string value, string expectedMessage)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseSetting(key, value));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>().WithMessage(expectedMessage);
    }

    private static JeebFirebaseContractOptions Canonical(
        string? projectId = null,
        string? databaseId = null,
        bool chatEnabled = true,
        string? pushProducer = null) => new()
        {
            SchemaVersion = JeebFirebaseContractOptions.CanonicalSchemaVersion,
            ProjectId = projectId ?? JeebFirebaseContractOptions.CanonicalProjectId,
            ProjectNumber = JeebFirebaseContractOptions.CanonicalProjectNumber,
            FirestoreDatabaseId = databaseId
                ?? JeebFirebaseContractOptions.CanonicalFirestoreDatabaseId,
            ChatEnabled = chatEnabled,
            PushProducer = pushProducer ?? JeebFirebaseContractOptions.CanonicalPushProducer,
        };

    private static string RepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, relativePath)))
        {
            current = current.Parent;
        }

        return current is null
            ? throw new DirectoryNotFoundException("Could not find the gateway repository root.")
            : Path.Combine(current.FullName, relativePath);
    }
}
