using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using SwServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Jeeb Partner Portal wallet BFF (partner-wallet-bff) endpoint tests. Every partner route is
/// exercised through the REAL host (<see cref="WebApplicationFactory{Program}"/>) with a fake
/// <see cref="SwServiceWalletClient"/> swapped in (the generated client's methods are <c>virtual</c>),
/// so the happy paths run fully offline — no wallet-service, no Docker. Negative paths (401/403/400)
/// are deterministic and never reach the fake. Auth mirrors the sibling endpoint tests: the
/// <c>X-User-Id</c>/<c>X-User-Roles</c> edge headers (trusted in the Development/Testing host).
/// </summary>
public sealed class PartnerWalletEndpointsTests
{
    private const string PartnerId = "11111111-1111-1111-1111-111111111111";
    private const string JeeberId = "22222222-2222-2222-2222-222222222222";

    private static WebApplicationFactory<Program> FactoryWithFakeWallet(FakeWalletClient fake)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddScoped<SwServiceWalletClient>(_ => fake)));

    private static HttpClient AsPartner(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", PartnerId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "partner");
        return c;
    }

    private static HttpClient AsAdmin(WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", "33333333-3333-3333-3333-333333333333");
        c.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return c;
    }

    // ── Happy paths (offline, via the fake wallet client) ─────────────────────────────

    [Fact]
    public async Task Partner_Topup_Executes_And_Returns_TransactionId()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = AsPartner(factory);

        var resp = await client.PostAsJsonAsync("/v1/partner/wallet/transfers", new
        {
            jeeberId = JeeberId,
            amount = 25.0,
            idempotencyKey = "idem-key-abcdef01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MoveDto>();
        body!.TransactionId.Should().NotBeEmpty();
        body.Amount.Should().Be(25.0);
        body.Status.Should().Be("executed");
    }

    [Fact]
    public async Task Partner_Predict_Returns_Fees_Preview()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient { PredictFees = 2.5 });
        using var client = AsPartner(factory);

        var resp = await client.PostAsJsonAsync("/v1/partner/wallet/transfers/predict", new
        {
            jeeberId = JeeberId,
            amount = 50.0,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PreviewDto>();
        body!.Fees.Should().Be(2.5);
        body.NetToJeeber.Should().Be(47.5);
    }

    [Fact]
    public async Task Partner_Balance_Returns_Projection()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient { Balance = 120.0 });
        using var client = AsPartner(factory);

        var resp = await client.GetAsync("/v1/partner/wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_CashCredit_Executes()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = AsAdmin(factory);

        var resp = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", new
        {
            amount = 300.0,
            evidenceNote = "cash handover receipt #A-1024",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MoveDto>();
        body!.Amount.Should().Be(300.0);
    }

    // ── Negative paths (deterministic; never reach the fake) ──────────────────────────

    [Fact]
    public async Task Partner_Topup_Without_Auth_Is_401()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = factory.CreateClient(); // no identity headers

        var resp = await client.PostAsJsonAsync("/v1/partner/wallet/transfers", new
        {
            jeeberId = JeeberId,
            amount = 25.0,
            idempotencyKey = "idem-key-abcdef01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Partner_Balance_With_Wrong_Role_Is_403()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", JeeberId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // jeeber, not partner

        var resp = await client.GetAsync("/v1/partner/wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Partner_Topup_With_Zero_Amount_Is_400()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = AsPartner(factory);

        var resp = await client.PostAsJsonAsync("/v1/partner/wallet/transfers", new
        {
            jeeberId = JeeberId,
            amount = 0.0, // fails [Range(0.01, ...)]
            idempotencyKey = "idem-key-abcdef01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_CashCredit_Without_EvidenceNote_Is_400()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = AsAdmin(factory);

        var resp = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", new
        {
            amount = 300.0, // missing mandatory evidenceNote → [Required] 400
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── DTOs + fake ───────────────────────────────────────────────────────────────────

    private sealed record MoveDto(Guid TransactionId, double Amount, double Fees, string Status);
    private sealed record PreviewDto(Guid JeeberId, double GrossAmount, double Fees, double NetToJeeber, string? Summary);

    /// <summary>
    /// Offline fake — overrides only the (virtual) wallet-service methods the partner BFF calls.
    /// Every holder read returns a single wallet (CurrencyID=1) so the wallet-id resolution succeeds;
    /// the saga initiate returns a header id the execute then "commits".
    /// </summary>
    private sealed class FakeWalletClient : SwServiceWalletClient
    {
        public double Balance { get; init; } = 0;
        public double PredictFees { get; init; } = 0;
        private static readonly Guid HeaderId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        public FakeWalletClient() : base("http://localhost", new HttpClient())
        {
        }

        private GetHolderWallets HolderWith(Guid holderId) => new()
        {
            WalletHolder = new WalletHolder { HolderId = holderId, HolderName = "fake", IsActive = true },
            Wallets = new List<Wallet>
            {
                new() { WalletId = Guid.NewGuid(), HolderId = holderId, CurrencyID = 1, Amount = Balance, Type = "main" },
            },
        };

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId)
            => Task.FromResult(HolderWith(holderId));

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
            => Task.FromResult(HolderWith(holderId));

        public override Task<AddWalletHolderResponse> SystemWalletAsync()
            => Task.FromResult(new AddWalletHolderResponse
            {
                Wallets = new List<Wallet>
                {
                    new() { WalletId = Guid.NewGuid(), CurrencyID = 1, Amount = 1_000_000, Type = "system" },
                },
            });

        public override Task<AddWalletHolderResponse> SystemWalletAsync(CancellationToken ct)
            => SystemWalletAsync();

        public override Task<ExpectedTransaction> PredictAsync(TransactionRequest body)
            => Task.FromResult(new ExpectedTransaction
            {
                GrossAmount = body.Transactions is { Count: > 0 } ? FirstAmount(body) : 0,
                Fees = PredictFees,
                Summary = "fake-preview",
            });

        public override Task<ExpectedTransaction> PredictAsync(TransactionRequest body, CancellationToken ct)
            => PredictAsync(body);

        public override Task<Transaction> InitiateAsync(TransactionRequest body)
            => Task.FromResult(new Transaction
            {
                TransactionHeader = new TransactionHeader { TxId = HeaderId, Status = 0 },
            });

        public override Task<Transaction> InitiateAsync(TransactionRequest body, CancellationToken ct)
            => InitiateAsync(body);

        public override Task ExecuteAsync(Guid transactionHeaderId) => Task.CompletedTask;
        public override Task ExecuteAsync(Guid transactionHeaderId, CancellationToken ct) => Task.CompletedTask;
        public override Task AbortAsync(Guid transactionHeaderId) => Task.CompletedTask;
        public override Task AbortAsync(Guid transactionHeaderId, CancellationToken ct) => Task.CompletedTask;

        private static double FirstAmount(TransactionRequest body)
        {
            foreach (var d in body.Transactions) return d.Amount;
            return 0;
        }
    }
}
