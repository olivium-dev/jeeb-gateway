using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

/// <summary>
/// A3 / gwdbx W3-01 — the Redis-down drill, run WITHOUT deploying. Door-OTP state (handover code +
/// at-door attempt/lockout counters) lives in the Redis IDistributedCache; a prod-like boot without
/// it must REFUSE, naming the key, instead of degrading to the in-process cache behind a green health
/// check. StoreDurabilityGuard cannot catch this — the store type is the same on both branches.
/// </summary>
public class RedisFailClosedBootGuardTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private const string RealRedis = "127.0.0.1:6379";

    // W5-11 deleted StoreDurabilityGuard; its escape-hatch key survives as this literal only.
    private const string StoreDurabilityFailClosedKey = "StoreDurability:FailClosedDisabled";

    // A real (>=32 byte) key so a Production host boot gets past JwtSigningKeyGuard and actually
    // reaches the Redis guard — otherwise the throw would be the JWT guard's and would prove nothing.
    private const string ProdJwtKey = "w3-01-production-boot-signing-key-32+chars";

    /// <summary>A25 — the shipped Production config must carry NO Redis endpoint: a baked-in value
    /// satisfies EnsureWired's set/non-placeholder check wherever it points, so the decommissioned
    /// host that sat here armed the guard against a black hole.</summary>
    [Fact]
    public void Production_Config_Commits_No_Redis_Endpoint_And_No_Decommissioned_Host()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "JeebGateway", "appsettings.Production.json");
        var raw = File.ReadAllText(path);

        raw.Should().NotContain("192.168.2.20",
            "the .20 box is decommissioned (A25); no committed config may reference it");

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("Redis", out var redis)
            && redis.TryGetProperty("ConnectionString", out var cs))
        {
            cs.GetString().Should().BeNullOrWhiteSpace(
                "the deploy env supplies the real endpoint; a committed default is exactly the "
                + "defect, because it satisfies the fail-closed guard while pointing anywhere");
        }
    }

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JeebGateway")))
            dir = dir.Parent!;

        (dir is not null).Should().BeTrue("could not locate repo root from test source file path");
        return dir!.FullName;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in entries) dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ---- the pure guard ---------------------------------------------------------------------

    [Fact]
    public void ProdLike_Without_Redis_Refuses_And_Names_The_Missing_Key()
    {
        var act = () => RedisDurabilityGuard.EnsureWired(Config(), new FakeEnv());

        var ex = Assert.Throws<InvalidOperationException>(act);
        ex.Message.Should().Contain(RedisDurabilityGuard.ConnectionStringKey,
            "an unnamed refusal does not tell the operator which key to set");
        ex.Message.Should().Contain("Redis__ConnectionString", "the deploy sets the env-var form");
        ex.Message.Should().Contain("door-OTP", "the refusal must say WHAT it is protecting");
        ex.Message.Should().Contain("Refusing to start");
    }

    /// <summary>The W1-14 residual, not repeated: the guard covers the WHOLE prod-like range, not
    /// just the literal name "Production".</summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("prod-canary")]
    [InlineData("PreProd")]
    public void Every_ProdLike_Environment_Name_Is_Armed(string environmentName)
    {
        var act = () => RedisDurabilityGuard.EnsureWired(
            Config(), new FakeEnv { EnvironmentName = environmentName });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*" + RedisDurabilityGuard.ConnectionStringKey + "*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Development_And_Testing_Keep_The_InProcess_Cache(string environmentName)
    {
        var act = () => RedisDurabilityGuard.EnsureWired(
            Config(), new FakeEnv { EnvironmentName = environmentName });

        act.Should().NotThrow("local runs and the CI harness legitimately use the memory cache");
    }

    [Fact]
    public void ProdLike_With_Redis_Wired_Does_Not_Refuse()
    {
        // POSITIVE CONTROL for the throws above: a guard that refused unconditionally would satisfy
        // them without checking anything. Only the key's presence changes between this and case 1.
        var act = () => RedisDurabilityGuard.EnsureWired(
            Config((RedisDurabilityGuard.ConnectionStringKey, RealRedis)), new FakeEnv());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("REPLACE-WITH-PRODUCTION-REDIS")]
    [InlineData("changeme:6379")]
    [InlineData("redis-placeholder:6379")]
    public void ProdLike_With_A_Placeholder_Value_Refuses(string connectionString)
    {
        var act = () => RedisDurabilityGuard.EnsureWired(
            Config((RedisDurabilityGuard.ConnectionStringKey, connectionString)), new FakeEnv());

        act.Should().Throw<InvalidOperationException>().WithMessage("*placeholder*");
    }

    /// <summary>Mirrors W18 S6: the StoreDurability hatch let the harness boot without
    /// PROVISIONING durable infrastructure. W5-11 deleted its guard, so the key is now inert —
    /// and an inert key must still not disarm the Redis guard.</summary>
    [Fact]
    public void The_StoreDurability_Escape_Hatch_Does_Not_Disarm_This_Guard()
    {
        var act = () => RedisDurabilityGuard.EnsureWired(
            Config((StoreDurabilityFailClosedKey, "true")), new FakeEnv());

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- the drill on a REAL host (no deploy) -----------------------------------------------

    private static WebApplicationFactory<Program> ProductionHost(string? redisConnectionString)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting(Fakes.ProductionMountedSecrets.StateServiceTokenFileKey,
                Fakes.ProductionMountedSecrets.StateServiceTokenFile);
            b.UseSetting("Jwt:SigningKey", ProdJwtKey);
            b.UseSetting("UmJwt:SigningKey", ProdJwtKey);
            b.UseSetting("BffServices:RequiredInProduction", "false");
            // Inert since W5-11 deleted the store-durability gate; kept so the single variable
            // between the two drills below stays the Redis key.
            b.UseSetting(StoreDurabilityFailClosedKey, "true");
            if (redisConnectionString is not null)
            {
                b.UseSetting(RedisDurabilityGuard.ConnectionStringKey, redisConnectionString);
            }
        });
    }

    private static string Flatten(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append(" | ");
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions) text.Append(inner.Message).Append(" | ");
            }
        }
        return text.ToString();
    }

    [Fact]
    public void RedisDown_Drill_A_Production_Host_With_The_Key_Blanked_Aborts_Explicitly()
    {
        // The dropped-env-var deploy. appsettings.Production.json no longer commits an endpoint
        // (A25), so blanking here just pins the explicitly-empty case too.
        using var factory = ProductionHost(redisConnectionString: "");

        var boot = Record.Exception(() => factory.CreateClient());

        boot.Should().NotBeNull("a prod-like gateway must refuse to hold door-OTP codes in process memory");
        var detail = Flatten(boot!);
        detail.Should().Contain(RedisDurabilityGuard.ConnectionStringKey,
            "the abort must name the missing key, not leave an operator guessing");
        detail.Should().Contain("FAIL-CLOSED (A3/W3-01)");
        detail.Should().NotContain("Unable to resolve service for type",
            "this must be an explicit refusal, NOT a generic DI resolution accident");
    }

    [Fact]
    public void RedisDown_Drill_B_The_Same_Production_Host_With_Redis_Wired_Boots()
    {
        // THE DISCRIMINATING CONTROL for drill A. Same host, same settings, one variable flipped:
        // if this also failed, drill A would prove nothing about Redis.
        using var factory = ProductionHost(redisConnectionString: RealRedis);

        var boot = Record.Exception(() => factory.CreateClient());

        boot.Should().BeNull("with Redis wired the Production host must boot normally");
    }

    [Fact]
    public void RedisDown_Drill_C_Testing_Host_Without_Redis_Still_Boots()
    {
        // The dev/CI fallback stays intact — the guard must not have made the memory cache unusable.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting(RedisDurabilityGuard.ConnectionStringKey, ""));

        Record.Exception(() => factory.CreateClient()).Should().BeNull();
    }
}
