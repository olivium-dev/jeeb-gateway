using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Operations.RealtimeProbe;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Operations;

public sealed class StagingRealtimeProbeProgramWiringTests
{
    [Fact]
    public async Task ExactDedicatedAuthorityConfiguration_BootsProgramAndMapsStagingRoute()
    {
        using var factory = StagingFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            StagingRealtimeProbeEndpoint.Route,
            content: null);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "the mapped staging route should reject missing HMAC headers, not be absent");
    }

    [Fact]
    public void InlineGuardianFallback_RefusesProgramBootDuringOptionsValidation()
    {
        using var factory = StagingFactory(
            inlineGuardian: new string('g', 64));

        var exception = Record.Exception(() => factory.CreateClient());

        exception.Should().NotBeNull();
        Flatten(exception!).Should().Contain(
            "exact dedicated Guardian and membership-ticket secret files");
    }

    private static global::JeebGateway.IntegrationTests.WebApplicationFactory<Program>
        StagingFactory(string inlineGuardian = "")
        => new global::JeebGateway.IntegrationTests.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Staging");
                builder.UseSetting("Jwt:SigningKey", new string('j', 48));
                builder.UseSetting("Jwt:SigningKeyFile", "");
                builder.UseSetting("UmJwt:SigningKey", "");
                builder.UseSetting("UmJwt:SigningKeyFile", "");
                builder.UseSetting("Redis:ConnectionString", "127.0.0.1:6379");
                builder.UseSetting("JeebStateService:Enabled", "true");
                builder.UseSetting("JeebStateService:BaseUrl", "http://state.test/");
                builder.UseSetting("JeebStateService:ServiceTokenFile", "");
                builder.UseSetting("BffServices:RequiredInProduction", "false");
                builder.UseSetting("AdminOidc:Enabled", "false");
                builder.UseSetting("Services:Geolocation:BaseUrl", "http://geo.test/");
                builder.UseSetting("Services:Settlement:BaseUrl", "http://settlement.test/");
                builder.UseSetting(
                    "Services:Settlement:ApiTokenFile",
                    "/tmp/jeeb-staging-test-settlement-token");
                builder.UseSetting("Services:Settlement:ApiToken", "");
                builder.UseSetting("Services:Realtime:GuardianSecret", inlineGuardian);
                builder.UseSetting(
                    "Services:Realtime:GuardianSecretFile",
                    RealtimeProbeCredentialConfigurationGuard.GuardianSecretFile);
                builder.UseSetting(
                    "Services:Realtime:MembershipTicketSigningKeyFile",
                    RealtimeProbeCredentialConfigurationGuard
                        .MembershipTicketSigningKeyFile);
                builder.UseSetting(
                    "Services:Realtime:GuardianIssuer",
                    RealtimeProbeDescriptorService.ExactGuardianIssuer);
                builder.UseSetting(
                    "Services:Realtime:TenantPrefix",
                    "jeeb");
                builder.UseSetting(
                    "Services:Realtime:PublicSocketUrl",
                    RealtimeProbeDescriptorService.ExactPublicSocketUrl);
                builder.UseSetting(
                    "Operations:RealtimeProbe:MintKeyFile",
                    RealtimeProbeOptions.RequiredMintKeyFile);
            });

    private static string Flatten(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            text.Append(current.Message).Append(" | ");
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    text.Append(inner.Message).Append(" | ");
                }
            }
        }

        return text.ToString();
    }
}
