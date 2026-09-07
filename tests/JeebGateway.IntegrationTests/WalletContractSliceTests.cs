using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.service.ServiceWallet;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class WalletContractSliceTests
{
    [Fact]
    public void PinnedSliceAndClientRetainExistingHoldPrimitives()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src/JeebGateway/contracts/wallet-service.openapi.json")));
        var paths = document.RootElement.GetProperty("paths");
        paths.EnumerateObject().Count().Should().BeGreaterThanOrEqualTo(28);
        foreach (var (path, method) in new[]
                 {
                     ("/Wallet/holder/ensure", "put"),
                     ("/Transaction/validate", "post"),
                     ("/Transaction/by-external-reference/{externalReference}", "get"),
                     ("/Transaction/{transactionHeaderId}/abort", "post"),
                     ("/Transaction/{transactionHeaderId}/execute", "post"),
                 })
        {
            paths.GetProperty(path).GetProperty(method).GetProperty("responses")
                .EnumerateObject().Should().NotBeEmpty();
        }
        typeof(ServiceWalletClient).GetMethods().Select(method => method.Name).Should().Contain(
            ["EnsureAsync", "ValidateAsync", "ByExternalReferenceAsync", "AbortAsync", "ExecuteAsync"]);
        new TransactionRequest().ApplyConfiguredFees.Should().BeTrue();
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
