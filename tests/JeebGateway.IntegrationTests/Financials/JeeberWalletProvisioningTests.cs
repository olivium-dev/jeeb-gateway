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

    [Fact]
    public async Task PhoneFindOrCreate_EnsuresWallets_ForResolvedUser()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events);
        var inner = new StubDualRole(events) { PhoneUserId = HolderId.ToString("D") };
        var sut = NewGuard(inner, wallet);

        var result = await sut.PhoneFindOrCreateAsync("+9613000077", CancellationToken.None);

        result.UserId.Should().Be(HolderId.ToString("D"));
        // Identity resolves first, then the wallet inventory for exactly that subject, once.
        events.Should().Equal("phone", "wallet");
        wallet.HolderIds.Should().Equal(HolderId);
    }

    [Fact]
    public async Task PhoneFindOrCreate_Succeeds_WhenWalletEnsureFails()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events) { Failure = new IOException("wallet down") };
        var inner = new StubDualRole(events) { PhoneUserId = HolderId.ToString("D") };
        var sut = NewGuard(inner, wallet);

        var result = await sut.PhoneFindOrCreateAsync("+9613000077", CancellationToken.None);

        // Best-effort: a missed ensure degrades to an honest 402 at submit, never a blocked login.
        result.UserId.Should().Be(HolderId.ToString("D"));
        result.ActiveRole.Should().Be(Roles.Client);
        events.Should().Equal("phone", "wallet");
        inner.PhoneCalls.Should().Be(1);
    }

    [Fact]
    public async Task PhoneFindOrCreate_Skips_Ensure_For_A_NonHolder_Subject_Without_Failing_Login()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events);
        var sut = NewGuard(new StubDualRole(events) { PhoneUserId = "legacy-user" }, wallet);

        var result = await sut.PhoneFindOrCreateAsync("+9613000077", CancellationToken.None);

        result.UserId.Should().Be("legacy-user");
        events.Should().Equal("phone");
        wallet.HolderIds.Should().BeEmpty();

        // Control: the all-zero system id is equally not a wallet holder, and equally not fatal.
        var systemEvents = new List<string>();
        var systemWallet = new StubProvisioner(systemEvents);
        var systemSut = NewGuard(
            new StubDualRole(systemEvents) { PhoneUserId = Guid.Empty.ToString("D") }, systemWallet);

        (await systemSut.PhoneFindOrCreateAsync("+9613000078", CancellationToken.None))
            .UserId.Should().Be(Guid.Empty.ToString("D"));
        systemEvents.Should().Equal("phone");
        systemWallet.HolderIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RoleSwitch_ToJeeber_EnsuresWallets()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events);
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        var result = await sut.RoleSwitchAsync(HolderId.ToString("D"), Roles.Jeeber, CancellationToken.None);

        // Ordering is the assertion: the wallet exists before the token says 'driver'.
        result.ActiveRole.Should().Be(Roles.Jeeber);
        events.Should().Equal("wallet", "switch");
        wallet.HolderIds.Should().Equal(HolderId);

        // Control: switching back to the client role provisions nothing.
        await sut.RoleSwitchAsync(HolderId.ToString("D"), Roles.Client, CancellationToken.None);

        events.Should().Equal("wallet", "switch", "switch");
        wallet.HolderIds.Should().Equal(HolderId);
    }

    [Fact]
    public async Task RoleSwitch_ToJeeber_Succeeds_WhenWalletEnsureFails()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events) { Failure = new IOException("wallet down") };
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        var result = await sut.RoleSwitchAsync(HolderId.ToString("D"), Roles.Jeeber, CancellationToken.None);

        // Same best-effort direction as signup: the switch is not a new availability coupling.
        result.ActiveRole.Should().Be(Roles.Jeeber);
        events.Should().Equal("wallet", "switch");
        inner.SwitchCalls.Should().Be(1);
    }

    [Fact]
    public async Task AppendJeeberRole_StillFailsClosed_WhenEnsureFails()
    {
        var events = new List<string>();
        var wallet = new StubProvisioner(events) { Failure = new IOException("wallet down") };
        var inner = new StubDualRole(events);
        var sut = NewGuard(inner, wallet);

        await sut.Invoking(value => value.AppendAvailableRoleAsync(
                HolderId.ToString("D"), Roles.Jeeber, CancellationToken.None))
            .Should().ThrowAsync<UserManagementCallException>()
            .Where(error => error.StatusCode == 502);

        // The GRANT seam stays fail-closed even though signup and role-switch turned best-effort.
        events.Should().Equal("wallet");
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

        public ProvisioningHandler(
            string? existingWalletType = null,
            bool duplicateActiveWallet = false)
        {
            _existingWalletType = existingWalletType;
            _duplicateActiveWallet = duplicateActiveWallet;
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
                    $"{{\"walletHolder\":{{\"holderId\":\"{HolderId:D}\",\"holderName\":\"legacy\",\"holderType\":\"person\",\"isActive\":true}},"
                    + $"\"wallets\":[{{\"walletId\":\"{WalletOne:D}\",\"currencyID\":2,\"type\":\"{_existingWalletType ?? "jeeb"}\",\"isActive\":true}}{duplicate}]}}");
            }
            if (request.Method == HttpMethod.Put && path == "/Wallet/holder/ensure")
            {
                EnsureBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var holderName = _existingWalletType is null ? HolderId.ToString("D") : "legacy";
                var holderType = _existingWalletType is null ? "jeeber" : "person";
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
        public int PhoneCalls { get; private set; }
        public int SwitchCalls { get; private set; }

        /// <summary>The id user-management resolves the phone to; not every subject is a holder GUID.</summary>
        public string PhoneUserId { get; init; } = HolderId.ToString("D");

        public Task<RoleGrantResult> AppendAvailableRoleAsync(
            string userId,
            string opaqueRole,
            CancellationToken ct)
        {
            events.Add("grant");
            var added = AppendCalls++ == 0;
            return Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, added));
        }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
        {
            events.Add("phone");
            var isNew = PhoneCalls++ == 0;
            return Task.FromResult(new PhoneFindOrCreateResult(
                PhoneUserId, isNew, new[] { Roles.Client }, Roles.Client));
        }

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(
            string userId,
            string opaqueRole,
            CancellationToken ct)
        {
            events.Add("switch");
            SwitchCalls++;
            return Task.FromResult(new RoleSwitchReissueResult(
                userId, "access-token", "refresh-token", opaqueRole));
        }

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(
            string userId,
            string opaqueRole,
            CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
