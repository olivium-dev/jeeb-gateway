using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.JeebWallet;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

public sealed class JeeberWalletProvisioningTests
{
    private static readonly Guid HolderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WalletOne = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WalletTwo = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task Ensure_Uses_Opaque_Id_And_All_Wallet_Currencies_Without_Auth()
    {
        var handler = new ProvisioningHandler();
        var provisioner = NewProvisioner(handler);

        await provisioner.EnsureAsync(HolderId, CancellationToken.None);

        handler.Paths.Should().Equal(
            "GET /Fees/currencies",
            $"GET /Wallet/holder/{HolderId:D}/wallets",
            "PUT /Wallet/holder/ensure");
        handler.Headers.Should().OnlyContain(header =>
            header.Authorization == null && header.ServiceAuth == null);

        using var request = JsonDocument.Parse(handler.EnsureBody!);
        var holder = request.RootElement.GetProperty("walletHolder");
        holder.GetProperty("holderId").GetGuid().Should().Be(HolderId);
        holder.GetProperty("holderName").GetString().Should().Be(HolderId.ToString("D"));
        holder.GetProperty("holderType").GetString().Should().Be("jeeber");
        var wallets = request.RootElement.GetProperty("wallets").EnumerateArray().ToArray();
        wallets.Select(wallet => wallet.GetProperty("currencyID").GetInt32())
            .Should().Equal(2, 7);
        wallets.Should().OnlyContain(wallet => wallet.GetProperty("type").GetString() == "jeeb");
    }

    [Fact]
    public async Task EnsurePartner_UsesPartnerHolderTypeAndRuntimeDisplayName()
    {
        var handler = new ProvisioningHandler();
        var provisioner = (IPartnerWalletProvisioner)NewProvisioner(handler);

        await provisioner.EnsureAsync(HolderId, "Dev Tool Partner", CancellationToken.None);

        using var request = JsonDocument.Parse(handler.EnsureBody!);
        var holder = request.RootElement.GetProperty("walletHolder");
        holder.GetProperty("holderId").GetGuid().Should().Be(HolderId);
        holder.GetProperty("holderName").GetString().Should().Be("Dev Tool Partner");
        holder.GetProperty("holderType").GetString().Should().Be("partner");
        request.RootElement.GetProperty("wallets").EnumerateArray()
            .Should().OnlyContain(wallet =>
                wallet.GetProperty("note").GetString() ==
                "devtool-partner-wallet-bootstrap");
    }

    [Fact]
    public async Task EnsurePartner_RejectsExistingOppositeHolderTypeBeforePut()
    {
        var handler = new ProvisioningHandler(
            existingWalletType: "legacy-cash",
            existingHolderType: "person");
        var provisioner = (IPartnerWalletProvisioner)NewProvisioner(handler);

        await provisioner.Invoking(value => value.EnsureAsync(
                HolderId,
                "Dev Tool Partner",
                CancellationToken.None))
            .Should().ThrowAsync<WalletProvisioningUnavailableException>()
            .WithMessage("*not 'partner'*");

        handler.EnsureBody.Should().BeNull();
    }

    [Fact]
    public async Task EnsureJeeber_RejectsExistingOppositeHolderTypeBeforePut()
    {
        var handler = new ProvisioningHandler(
            existingWalletType: "legacy-cash",
            existingHolderType: "person");
        var provisioner = NewProvisioner(handler);

        await provisioner.Invoking(value => value.EnsureAsync(
                HolderId,
                CancellationToken.None))
            .Should().ThrowAsync<WalletProvisioningUnavailableException>()
            .WithMessage("*not 'jeeber'*");

        handler.EnsureBody.Should().BeNull();
    }

    [Fact]
    public async Task EnsurePartner_RejectsExistingHolderWithMissingActorTypeBeforePut()
    {
        var handler = new ProvisioningHandler(
            existingWalletType: "legacy-cash",
            existingHolderType: "");
        var provisioner = (IPartnerWalletProvisioner)NewProvisioner(handler);

        await provisioner.Invoking(value => value.EnsureAsync(
                HolderId,
                "Dev Tool Partner",
                CancellationToken.None))
            .Should().ThrowAsync<WalletProvisioningUnavailableException>()
            .WithMessage("*missing its actor type*");

        handler.EnsureBody.Should().BeNull();
    }

    [Fact]
    public async Task Ensure_Reuses_The_Only_Existing_Active_Wallet_Type()
    {
        var handler = new ProvisioningHandler(existingWalletType: "legacy-cash");
        var provisioner = NewProvisioner(handler);

        await provisioner.EnsureAsync(HolderId, CancellationToken.None);

        using var request = JsonDocument.Parse(handler.EnsureBody!);
        var wallets = request.RootElement.GetProperty("wallets").EnumerateArray().ToArray();
        wallets.Single(wallet => wallet.GetProperty("currencyID").GetInt32() == 2)
            .GetProperty("type").GetString().Should().Be("legacy-cash");
        wallets.Single(wallet => wallet.GetProperty("currencyID").GetInt32() == 7)
            .GetProperty("type").GetString().Should().Be("jeeb");
    }

    [Fact]
    public async Task Ensure_Rejects_Ambiguous_Inventory_Before_Put()
    {
        var handler = new ProvisioningHandler(duplicateActiveWallet: true);
        var provisioner = NewProvisioner(handler);

        await provisioner.Invoking(value => value.EnsureAsync(HolderId, CancellationToken.None))
            .Should().ThrowAsync<WalletProvisioningUnavailableException>()
            .WithMessage("*multiple active wallets*");

        handler.EnsureBody.Should().BeNull();
    }

    [Fact]
    public async Task Driver_Grant_Provisions_Before_Forwarding_And_Replays_Safely()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events);
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        var first = await sut.AppendAvailableRoleAsync(
            HolderId.ToString("D"), Roles.Jeeber, CancellationToken.None);
        var replay = await sut.AppendAvailableRoleAsync(
            HolderId.ToString("D"), Roles.Jeeber, CancellationToken.None);

        first.Added.Should().BeTrue();
        replay.Added.Should().BeFalse();
        events.Should().Equal("wallet", "grant", "wallet", "grant");
        wallet.HolderIds.Should().Equal(HolderId, HolderId);
    }

    [Fact]
    public async Task Provisioning_Failure_Prevents_Driver_Grant()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events) { Failure = new IOException("wallet down") };
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        await sut.Invoking(value => value.AppendAvailableRoleAsync(
                HolderId.ToString("D"), Roles.Jeeber, CancellationToken.None))
            .Should().ThrowAsync<UserManagementCallException>()
            .Where(error => error.StatusCode == 502);

        events.Should().Equal("wallet");
        inner.AppendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Non_Driver_Grant_Bypasses_Wallet_Provisioning()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events);
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        await sut.AppendAvailableRoleAsync(HolderId.ToString("D"), Roles.Client, CancellationToken.None);

        events.Should().Equal("grant");
        wallet.HolderIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Driver_Grant_Rejects_A_NonUuid_Subject_Before_Any_Upstream_Mutation()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events);
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        await sut.Invoking(value => value.AppendAvailableRoleAsync(
                "legacy-user", Roles.Jeeber, CancellationToken.None))
            .Should().ThrowAsync<UserManagementCallException>()
            .Where(error => error.StatusCode == 502);

        events.Should().BeEmpty();
        inner.AppendCalls.Should().Be(0);
    }

    private static WalletServiceJeeberWalletProvisioner NewProvisioner(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://wallet.test/") };
        return new WalletServiceJeeberWalletProvisioner(new FixedHttpClientFactory(http));
    }

    private static WalletProvisioningDualRoleClient NewGuard(
        IUserManagementDualRoleClient inner,
        IJeeberWalletProvisioner wallet) =>
        new(inner, wallet, NullLogger<WalletProvisioningDualRoleClient>.Instance);

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.Should().Be(WalletServiceJeeberWalletProvisioner.HttpClientName);
            return client;
        }
    }

    private sealed class ProvisioningHandler : HttpMessageHandler
    {
        private readonly string? _existingWalletType;
        private readonly bool _duplicateActiveWallet;
        private readonly string _existingHolderType;

        public ProvisioningHandler(
            string? existingWalletType = null,
            bool duplicateActiveWallet = false,
            string existingHolderType = "jeeber")
        {
            _existingWalletType = existingWalletType;
            _duplicateActiveWallet = duplicateActiveWallet;
            _existingHolderType = existingHolderType;
        }

        public List<string> Paths { get; } = new();
        public List<CapturedHeaders> Headers { get; } = new();
        public string? EnsureBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            Headers.Add(new CapturedHeaders(
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Service-Auth", out var values)
                    ? values.Single()
                    : null));

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/Fees/currencies")
                return Json("[{\"id\":2,\"code\":\"USD\"},{\"id\":7,\"code\":\"EUR\"}]");
            if (request.Method == HttpMethod.Get
                && path == $"/Wallet/holder/{HolderId:D}/wallets")
            {
                if (_existingWalletType is null && !_duplicateActiveWallet)
                    return Json("{}");

                var duplicate = _duplicateActiveWallet
                    ? $",{{\"walletId\":\"{Guid.NewGuid():D}\",\"currencyID\":2,\"type\":\"other\",\"isActive\":true}}"
                    : string.Empty;
                return Json(
                    $"{{\"walletHolder\":{{\"holderId\":\"{HolderId:D}\",\"holderName\":\"legacy\",\"holderType\":\"{_existingHolderType}\",\"isActive\":true}},"
                    + $"\"wallets\":[{{\"walletId\":\"{WalletOne:D}\",\"currencyID\":2,\"type\":\"{_existingWalletType ?? "jeeb"}\",\"isActive\":true}}{duplicate}]}}");
            }
            if (request.Method == HttpMethod.Put && path == "/Wallet/holder/ensure")
            {
                EnsureBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(EnsureBody);
                var requestedHolder = body.RootElement.GetProperty("walletHolder");
                var holderName = requestedHolder.GetProperty("holderName").GetString();
                var holderType = requestedHolder.GetProperty("holderType").GetString();
                var firstType = _existingWalletType ?? "jeeb";
                return Json(
                    $"{{\"walletHolder\":{{\"holderId\":\"{HolderId:D}\",\"holderName\":\"{holderName}\",\"holderType\":\"{holderType}\",\"isActive\":true}},"
                    + $"\"wallets\":[{{\"walletId\":\"{WalletOne:D}\",\"currencyID\":2,\"type\":\"{firstType}\",\"isActive\":true}},"
                    + $"{{\"walletId\":\"{WalletTwo:D}\",\"currencyID\":7,\"type\":\"jeeb\",\"isActive\":true}}]}}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record CapturedHeaders(string? Authorization, string? ServiceAuth);

    private sealed class StubProvisioner(List<string> events) : IJeeberWalletProvisioner
    {
        public Exception? Failure { get; init; }
        public List<Guid> HolderIds { get; } = new();

        public Task EnsureAsync(Guid holderId, CancellationToken ct)
        {
            events.Add("wallet");
            HolderIds.Add(holderId);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class StubDualRole(List<string> events) : IUserManagementDualRoleClient
    {
        public int AppendCalls { get; private set; }

        public Task<RoleGrantResult> AppendAvailableRoleAsync(
            string userId,
            string opaqueRole,
            CancellationToken ct)
        {
            events.Add("grant");
            var added = AppendCalls++ == 0;
            return Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, added));
        }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(
            string userId,
            string opaqueRole,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(
            string userId,
            string opaqueRole,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
