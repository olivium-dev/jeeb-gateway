using System;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Security;

/// <summary>
/// SEC-H2 (Leg-11) — the gateway must refuse to boot with a placeholder / dev-default / too-short
/// JWT signing key in any non-Development/Testing environment. Both committed placeholders are ≥32
/// bytes, so the length check alone would let a deploy that forgot to inject the real secret boot
/// with a publicly-known key → token forgery. The guard bakes no key; it only asserts one was
/// supplied from configuration.
/// </summary>
public class JwtPlaceholderKeyFailClosedTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("REPLACE-WITH-PRODUCTION-SIGNING-KEY-32+")] // committed appsettings placeholder
    [InlineData("dev-only-signing-key-32-bytes-minimum!!")] // JwtOptions code default
    [InlineData("")]                                          // missing
    [InlineData("too-short")]                                 // < 32 bytes
    public void Guard_Rejects_Placeholder_Or_Weak_Key_In_Production(string key)
    {
        var act = () => JwtSigningKeyGuard.EnsureNotPlaceholder(key, new FakeEnv { EnvironmentName = "Production" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*signing key*", "a non-secret key in production must fail closed");
    }

    [Fact]
    public void Guard_Accepts_Real_Key_In_Production()
    {
        var realKey = "a-genuinely-injected-32-byte-plus-secret-key";
        Encoding.UTF8.GetBytes(realKey).Length.Should().BeGreaterThanOrEqualTo(32);

        var act = () => JwtSigningKeyGuard.EnsureNotPlaceholder(realKey, new FakeEnv { EnvironmentName = "Production" });

        act.Should().NotThrow();
    }

    [Fact]
    public void Guard_Is_NoOp_In_Development_And_Testing()
    {
        // Local dev + the integration-test harness legitimately use the dev default.
        var placeholder = "dev-only-signing-key-32-bytes-minimum!!";
        var devAct = () => JwtSigningKeyGuard.EnsureNotPlaceholder(placeholder, new FakeEnv { EnvironmentName = Environments.Development });
        var testAct = () => JwtSigningKeyGuard.EnsureNotPlaceholder(placeholder, new FakeEnv { EnvironmentName = "Testing" });

        devAct.Should().NotThrow();
        testAct.Should().NotThrow();
    }

    [Fact]
    public void Production_Host_Refuses_To_Boot_With_Placeholder_Key()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "REPLACE-WITH-PRODUCTION-SIGNING-KEY-32+",
            }));
        });

        var ex = Record.Exception(() => factory.CreateClient());

        ex.Should().NotBeNull("booting in Production with the committed placeholder key must fail closed (SEC-H2)");
        Flatten(ex!).Should().Contain("SigningKey", "the boot failure must be the signing-key guard, not an unrelated error");
    }

    private static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e.Message).Append(" | ");
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.Flatten().InnerExceptions)
                {
                    sb.Append(inner.Message).Append(" | ");
                }
            }
        }
        return sb.ToString();
    }
}
