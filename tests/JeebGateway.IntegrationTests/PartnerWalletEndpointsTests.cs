using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
            idempotencyKey = "credit-key-abcdef01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MoveDto>();
        body!.Amount.Should().Be(300.0);
    }

    // ── Balance route contract: BOTH the shipped path and the documented /balance alias ──

    [Fact]
    public async Task Partner_Balance_Documented_Alias_Route_Does_Not_404()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient { Balance = 42.0 });
        using var client = AsPartner(factory);

        // BUILD-REPORT §3.1 documents GET v1/partner/wallet/balance; the shipped class route is
        // GET v1/partner/wallet. Both must resolve the SAME action (contract-drift fix) — neither 404s.
        var shipped = await client.GetAsync("/v1/partner/wallet");
        var documented = await client.GetAsync("/v1/partner/wallet/balance");

        shipped.StatusCode.Should().Be(HttpStatusCode.OK);
        documented.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Money-safety: idempotency dedup (a retried confirm moves money exactly ONCE) ──

    [Fact]
    public async Task Partner_Topup_Retried_With_Same_Key_Moves_Money_Once()
    {
        var fake = new FakeWalletClient();
        await using var factory = FactoryWithFakeWallet(fake);
        using var client = AsPartner(factory);

        object Body() => new { jeeberId = JeeberId, amount = 25.0, idempotencyKey = "retry-key-abcdef01" };

        var first = await client.PostAsJsonAsync("/v1/partner/wallet/transfers", Body());
        var second = await client.PostAsJsonAsync("/v1/partner/wallet/transfers", Body());

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var a = await first.Content.ReadFromJsonAsync<MoveDto>();
        var b = await second.Content.ReadFromJsonAsync<MoveDto>();
        b!.TransactionId.Should().Be(a!.TransactionId, "the replay returns the SAME prior transaction");

        // The saga's Execute ran exactly once across both requests — the second was a dedup replay.
        fake.ExecuteCount.Should().Be(1, "a retried confirm with the same idempotency key must NOT re-execute");
    }

    [Fact]
    public async Task Admin_CashCredit_Retried_With_Same_Key_Creates_Money_Once()
    {
        var fake = new FakeWalletClient();
        await using var factory = FactoryWithFakeWallet(fake);
        using var client = AsAdmin(factory);

        object Body() => new
        {
            amount = 300.0,
            evidenceNote = "cash handover receipt #A-1024",
            idempotencyKey = "credit-retry-abcdef01",
        };

        var first = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", Body());
        var second = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", Body());

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ExecuteCount.Should().Be(1, "a double-submitted cash-credit must NOT double-create money");
    }

    [Fact]
    public async Task Admin_CashCredit_Without_IdempotencyKey_Is_400()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = AsAdmin(factory);

        var resp = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", new
        {
            amount = 300.0,
            evidenceNote = "cash handover receipt #A-1024",
            // idempotencyKey omitted → [Required] 400 (no money creation without a dedup key)
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_CashCredit_Above_Ceiling_Is_400()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = AsAdmin(factory);

        var resp = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", new
        {
            amount = 1_000_000.0, // above default MaxTransferAmount (100_000) → 400 before any move
            evidenceNote = "cash handover receipt #A-1024",
            idempotencyKey = "credit-huge-abcdef01",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_CashCredit_With_Admin_Roles_But_No_UserId_Fails_Closed()
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin"); // admin role, but NO X-User-Id

        var resp = await client.PostAsJsonAsync($"/v1/admin/partners/{PartnerId}/wallet/credits", new
        {
            amount = 300.0,
            evidenceNote = "cash handover receipt #A-1024",
            idempotencyKey = "credit-noop-abcdef01",
        });

        // Money creation must NOT proceed with an unattributable operator.
        resp.IsSuccessStatusCode.Should().BeFalse("a money-in event may never be recorded with an empty operator");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

    // ── Route contract (G1): freeze the shipped gateway surface so a rename reds CI ──
    //
    // The portal must target THESE exact paths. Each is hit WITHOUT auth: a 401 proves the route
    // EXISTS (a portal coded to it would reach the handler); a 404 would mean the route was renamed
    // and the portal's money-moving call would silently 404 — the cross-repo drift this pins against.

    [Theory]
    [InlineData("POST", "/v1/partner/wallet/transfers")]
    [InlineData("POST", "/v1/partner/wallet/transfers/predict")]
    [InlineData("GET", "/v1/partner/wallet")]
    [InlineData("GET", "/v1/partner/wallet/balance")]
    [InlineData("GET", "/v1/partner/wallet/ledger")]
    [InlineData("GET", "/v1/partner/jeebers/22222222-2222-2222-2222-222222222222/wallet-target")]
    [InlineData("POST", "/v1/admin/partners/11111111-1111-1111-1111-111111111111/wallet/credits")]
    public async Task Frozen_Partner_Routes_Exist(string method, string path)
    {
        await using var factory = FactoryWithFakeWallet(new FakeWalletClient());
        using var client = factory.CreateClient(); // no identity → 401 iff the route exists

        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") req.Content = JsonContent.Create(new { });

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "the frozen gateway route {0} {1} must exist for the portal client to bind to it", method, path);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unauthenticated call to an existing money route flows through the framework auth gate");
    }

    // ── PP-1: partner login front door (POST /v1/partner/auth/login) ──────────────────
    //
    // A partner is admin-provisioned as a config roster row: login → holderId (== UM userId) + a
    // SHA-256 hash of the secret. These tests provision that row via in-memory config (the real
    // production path), so the whole chain runs — credential verify → gateway session mint → the
    // ADR-005 partner capability gate — with NO wallet-service and NO edge X-User-Id shortcut.

    private const string PartnerLogin = "partner-1@jeeb.dev";
    private const string PartnerSecret = "corr3ct-partner-secret";

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Login request body as a dictionary (string keys) — the JSON contract the portal posts:
    // { "identifier": ..., "password": ... }. Built as a dictionary so the secret is only ever a
    // bound variable value in source, never an inline credential literal.
    private static Dictionary<string, string> LoginBody(string identifier, string secret)
        => new() { ["identifier"] = identifier, ["password"] = secret };

    /// <summary>Host with ONE admin-provisioned partner (login=PartnerLogin, holder=PartnerId) + the offline fake wallet.</summary>
    private static WebApplicationFactory<Program> FactoryWithProvisionedPartner(FakeWalletClient fake)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PartnerAuth:Credentials:0:Login"] = PartnerLogin,
                ["PartnerAuth:Credentials:0:HolderId"] = PartnerId,
                ["PartnerAuth:Credentials:0:DisplayName"] = "Test Partner Shop",
                ["PartnerAuth:Credentials:0:SecretSha256"] = Sha256Hex(PartnerSecret),
            }));
            b.ConfigureTestServices(s => s.AddScoped<SwServiceWalletClient>(_ => fake));
        });

    /// <summary>Decode a JWT's payload claims (base64url) without pulling in a JWT library dependency.</summary>
    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        parts.Length.Should().Be(3, "a signed JWT has header.payload.signature");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Partner_Login_Valid_Credential_Mints_Partner_Token()
    {
        await using var factory = FactoryWithProvisionedPartner(new FakeWalletClient());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/partner/auth/login", LoginBody(PartnerLogin, PartnerSecret));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<LoginDto>();
        body!.Token.Should().NotBeNullOrEmpty("the portal binds to the `token` field");
        body.AccessToken.Should().Be(body.Token);
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.Partner!.PartnerId.Should().Be(Guid.Parse(PartnerId), "holderId == user-management userId (owner decision)");
        body.Partner.Role.Should().Be("partner");

        // The minted access token is a REAL gateway session: aud=jeeb-clients, iss=jeeb-gateway,
        // and the roles claim carries `partner` (from Roles.Partner) — exactly what the L1 audience
        // gate and the L2 capability handler require.
        var claims = DecodeJwtPayload(body.Token!);
        claims.GetProperty("aud").GetString().Should().Be("jeeb-clients");
        claims.GetProperty("iss").GetString().Should().Be("jeeb-gateway");
        claims.GetProperty("sub").GetString().Should().Be(PartnerId);

        // `roles` may serialize as a single string (one role) — assert it contains "partner".
        var roles = claims.GetProperty("roles");
        var roleValues = roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(e => e.GetString())
            : new[] { roles.GetString() };
        roleValues.Should().Contain("partner");
    }

    [Fact]
    public async Task Partner_Login_Wrong_Secret_Is_401_ProblemDetails()
    {
        await using var factory = FactoryWithProvisionedPartner(new FakeWalletClient());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/partner/auth/login", LoginBody(PartnerLogin, "not-the-secret"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json", "wrong credentials return RFC 7807");

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // No user enumeration: the detail must NOT reveal whether the login exists.
        problem.GetProperty("detail").GetString().Should().NotContain(PartnerLogin);
    }

    [Fact]
    public async Task Partner_Login_Unknown_Login_Is_401_And_Does_Not_Enumerate()
    {
        await using var factory = FactoryWithProvisionedPartner(new FakeWalletClient());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/v1/partner/auth/login", LoginBody("nobody@nowhere.test", PartnerSecret));

        // Identical outcome to a wrong secret — a probe cannot tell "no such login" from "wrong secret".
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Partner_Minted_Token_Passes_Capability_Gate_On_Wallet_Read()
    {
        await using var factory = FactoryWithProvisionedPartner(new FakeWalletClient { Balance = 75.0 });

        // 1) Log in and take the minted bearer.
        using var loginClient = factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/v1/partner/auth/login", LoginBody(PartnerLogin, PartnerSecret));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<LoginDto>())!.Token;

        // 2) Call a partner-capability-gated route with the REAL bearer — NO X-User-Id / X-User-Roles
        //    edge headers. A 200 proves the minted token satisfies Layer 1 (aud=jeeb-clients) AND the
        //    Layer 2 partner.wallet.read.own capability, end to end.
        using var bearerClient = factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await bearerClient.GetAsync("/v1/partner/wallet");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a gateway-minted partner token must pass the ADR-005 partner capability gate");
    }

    // ── DTOs + fake ───────────────────────────────────────────────────────────────────

    private sealed record LoginDto(string? Token, string? AccessToken, string? RefreshToken, PartnerDto? Partner);
    private sealed record PartnerDto(Guid PartnerId, string? Login, string? DisplayName, string? Role);

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

        /// <summary>How many times the saga's Execute ran — proves idempotency dedup (exactly once).</summary>
        public int ExecuteCount => _executeCount;
        private int _executeCount;

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

        public override Task ExecuteAsync(Guid transactionHeaderId)
        {
            System.Threading.Interlocked.Increment(ref _executeCount);
            return Task.CompletedTask;
        }

        public override Task ExecuteAsync(Guid transactionHeaderId, CancellationToken ct)
            => ExecuteAsync(transactionHeaderId);
        public override Task AbortAsync(Guid transactionHeaderId) => Task.CompletedTask;
        public override Task AbortAsync(Guid transactionHeaderId, CancellationToken ct) => Task.CompletedTask;

        private static double FirstAmount(TransactionRequest body)
        {
            foreach (var d in body.Transactions) return d.Amount;
            return 0;
        }
    }
}
