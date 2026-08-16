using System.Net;
using FluentAssertions;
using JeebGateway.Jobs;
using JeebGateway.Users;
using JeebGateway.Users.DataExport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Users;

public sealed class WalletServiceFinancialLedgerAnonymizerTests
{
    [Fact]
    public async Task Success_Uses_Exact_Owner_Close_Contract_And_Does_Not_Claim_A_Row_Count()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(handler);
        var holderId = Guid.NewGuid();

        var count = await client.AnonymizeForUserAsync(
            holderId.ToString("D"),
            "gateway-delivery-pseudonym-is-not-forwarded",
            CancellationToken.None);

        count.Should().Be(0, "wallet-service does not expose a rewritten-row count");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Path.Should().Be($"/Wallet/holder/{holderId:D}/close");
        handler.Requests[0].Body.Should().BeNull();
    }

    [Fact]
    public async Task Successful_Replay_Is_Treated_As_The_Same_Completed_Owner_Operation()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(handler);
        var holderId = Guid.NewGuid().ToString("D");

        await client.AnonymizeForUserAsync(holderId, "unused-one", CancellationToken.None);
        await client.AnonymizeForUserAsync(holderId, "unused-two", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Select(request => request.Path).Distinct()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Nonempty_409_Defers_Deletion_Without_Reporting_Success()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict));
        var client = Client(handler);

        var act = () => client.AnonymizeForUserAsync(
            Guid.NewGuid().ToString("D"),
            "unused",
            CancellationToken.None);

        await act.Should().ThrowAsync<WalletLedgerCloseConflictException>();
    }

    [Fact]
    public void Runtime_Program_Resolves_Only_Owner_Backed_Gdpr_Workflows()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
            });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFinancialLedgerAnonymizer>()
            .Should().BeOfType<WalletServiceFinancialLedgerAnonymizer>();
        scope.ServiceProvider.GetRequiredService<IAccountDeletionStore>()
            .Should().BeOfType<RemoteUserPreferencesAccountDeletionStore>();
        scope.ServiceProvider.GetRequiredService<IDataExportWorkflow>()
            .Should().BeOfType<StateDataExportWorkflow>();
        scope.ServiceProvider.GetRequiredService<DurableWorkSweepExecutor>()
            .Should().NotBeNull();
    }

    private static WalletServiceFinancialLedgerAnonymizer Client(
        RecordingHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://wallet-owner.test/"),
        };
        return new WalletServiceFinancialLedgerAnonymizer(
            new FixedHttpClientFactory(http));
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(WalletServiceFinancialLedgerAnonymizer.HttpClientName);
            return client;
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Content?.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter().GetResult()));
            return Task.FromResult(response(request));
        }
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string? Body);
}
