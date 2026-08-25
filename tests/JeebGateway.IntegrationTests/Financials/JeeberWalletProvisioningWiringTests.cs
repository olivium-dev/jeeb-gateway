using FluentAssertions;
using JeebGateway.JeebWallet;
using JeebGateway.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// D8 — the composition-root half that <see cref="JeeberWalletProvisioningTests"/> never covered:
/// those tests hand-build the guard, so they stayed green while merge 84002ac silently dropped its
/// DI registration and left every jeeber without a wallet.
/// </summary>
public sealed class JeeberWalletProvisioningWiringTests
{
    private const string WalletBaseUrl = "http://127.0.0.1:65001";

    // The suite's own WebApplicationFactory shadow swaps IUserManagementDualRoleClient for a fake,
    // so the FRAMEWORK factory is used here: only it exercises the real production composition.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        Action<IServiceCollection>? extra = null) =>
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("WalletServiceApi:BaseUrl", WalletBaseUrl);
                // Closed local port: the inner forward fails fast instead of resolving a real host.
                builder.UseSetting("UserManagementServiceApi:BaseUrl", "http://127.0.0.1:65002");
                builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
                if (extra is not null) builder.ConfigureServices(extra);
            });

    [Fact]
    public void Container_resolves_the_wallet_guard_in_front_of_the_role_authority()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>()
            .Should().BeOfType<WalletProvisioningDualRoleClient>(
                "a driver role grant must not be able to land without a wallet");
    }

    [Fact]
    public void Container_resolves_the_wallet_service_backed_provisioner()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IJeeberWalletProvisioner>()
            .Should().BeOfType<WalletServiceJeeberWalletProvisioner>();
    }

    [Fact]
    public void Provisioning_http_client_is_named_and_bound_to_the_wallet_base_url()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var clients = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        clients.CreateClient(WalletServiceJeeberWalletProvisioner.HttpClientName)
            .BaseAddress.Should().Be(new Uri(WalletBaseUrl + "/"));

        // Control: an unregistered name in the SAME container yields no BaseAddress, so the
        // assertion above discriminates a real registration rather than passing for any name.
        clients.CreateClient(WalletServiceJeeberWalletProvisioner.HttpClientName + "-unregistered")
            .BaseAddress.Should().BeNull();
    }

    [Fact]
    public async Task Driver_grant_routed_through_the_container_reaches_the_provisioner()
    {
        var recorder = new RecordingProvisioner();
        using var factory = Factory(services =>
        {
            services.RemoveAll<IJeeberWalletProvisioner>();
            services.AddSingleton<IJeeberWalletProvisioner>(recorder);
        });
        using var scope = factory.Services.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>();
        var subject = Guid.NewGuid();

        // The inner forward is a live user-management call and is expected to fail here; the
        // assertion is only about what happened BEFORE it.
        await Swallow(() => sut.AppendAvailableRoleAsync(subject.ToString("D"), Roles.Jeeber, default));

        recorder.Ensured.Should().Equal(subject);

        // Control: the same composed client, same recorder, must NOT provision for a non-jeeber
        // role — so the assertion above cannot be satisfied by a recorder that records everything.
        await Swallow(() => sut.AppendAvailableRoleAsync(Guid.NewGuid().ToString("D"), Roles.Client, default));

        recorder.Ensured.Should().Equal(subject);
    }

    [Fact]
    public async Task Role_switch_to_jeeber_routed_through_the_container_reaches_the_provisioner()
    {
        var recorder = new RecordingProvisioner();
        using var factory = Factory(services =>
        {
            services.RemoveAll<IJeeberWalletProvisioner>();
            services.AddSingleton<IJeeberWalletProvisioner>(recorder);
        });
        using var scope = factory.Services.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<IUserManagementDualRoleClient>();
        var subject = Guid.NewGuid();

        // The inner re-issue is a live user-management call and is expected to fail here; the
        // assertion is only about what happened BEFORE it.
        await Swallow(() => sut.RoleSwitchAsync(subject.ToString("D"), Roles.Jeeber, default));

        recorder.Ensured.Should().Equal(subject);

        // Control: a switch back to the client role must NOT provision, so the assertion above
        // cannot be satisfied by a recorder that records every switch.
        await Swallow(() => sut.RoleSwitchAsync(Guid.NewGuid().ToString("D"), Roles.Client, default));

        recorder.Ensured.Should().Equal(subject);
    }

    private static async Task Swallow(Func<Task> call)
    {
        try { await call(); } catch { /* inner transport outcome is out of scope */ }
    }

    private sealed class RecordingProvisioner : IJeeberWalletProvisioner
    {
        public List<Guid> Ensured { get; } = new();

        public Task EnsureAsync(Guid holderId, CancellationToken ct)
        {
            Ensured.Add(holderId);
            return Task.CompletedTask;
        }
    }
}
