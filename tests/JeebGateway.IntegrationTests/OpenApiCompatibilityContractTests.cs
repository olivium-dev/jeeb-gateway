using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.service.ServiceWallet;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class OpenApiCompatibilityContractTests
{
    private static readonly HashSet<string> HttpMethods = new(
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"],
        StringComparer.Ordinal);

    [Fact]
    public void GatewayArtifactRetainsEveryReviewedBasePathAndMethod()
    {
        var root = RepositoryRoot();
        var baseline = File.ReadAllLines(Path.Combine(
            root,
            "tests/JeebGateway.IntegrationTests/Contracts/jeeb-gateway.path-methods.baseline.txt"));
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "artifacts/openapi/jeeb-gateway.v1.json")));
        var candidate = artifact.RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => HttpMethods.Contains(operation.Name))
                .Select(operation => $"{operation.Name} {path.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        baseline.Except(candidate).Should().BeEmpty(
            "an additive wallet contract cannot remove an existing concrete gateway route");
    }

    [Fact]
    public void WalletProviderContractIsPinnedAndGeneratedForFeeSuppressionAndHolderEnsure()
    {
        var root = RepositoryRoot();
        using var provider = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/JeebGateway/contracts/wallet-service.openapi.json")));
        var feePolicy = provider.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("TransactionRequest").GetProperty("properties")
            .GetProperty("applyConfiguredFees");

        feePolicy.GetProperty("type").GetString().Should().Be("boolean");
        feePolicy.GetProperty("default").GetBoolean().Should().BeTrue();
        provider.RootElement.GetProperty("paths")
            .GetProperty("/Wallet/holder/ensure").TryGetProperty("put", out _)
            .Should().BeTrue();
        new TransactionRequest().ApplyConfiguredFees.Should().BeTrue();
        typeof(ServiceWalletClient).GetMethods()
            .Should().Contain(method => method.Name == "EnsureAsync");
    }

    [Fact]
    public void MobilePreviewPolicyIsRequiredInTheGatewayArtifact()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "artifacts/openapi/jeeb-gateway.v1.json")));
        var required = artifact.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("PartnerTopupPreviewResponse").GetProperty("required")
            .EnumerateArray().Select(value => value.GetString());

        required.Should().Contain("otpRequired");
    }

    [Fact]
    public void WalletMoneyRequestsPublishTheRuntimeMinorUnitConstraint()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "artifacts/openapi/jeeb-gateway.v1.json")));
        var schemas = artifact.RootElement
            .GetProperty("components").GetProperty("schemas");

        foreach (var schemaName in new[]
                 {
                     "PartnerTopupPredictRequest",
                     "PartnerTopupExecuteRequest",
                     "PartnerOtpChallengeRequest",
                     "PartnerCashCreditRequest",
                 })
        {
            schemas.GetProperty(schemaName).GetProperty("properties")
                .GetProperty("amount").GetProperty("multipleOf").GetDecimal()
                .Should().Be(0.01m, $"{schemaName} must publish the same two-decimal rule enforced at runtime");
        }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
