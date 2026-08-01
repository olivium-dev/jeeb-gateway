using System.Diagnostics;
using FluentAssertions;
using JeebGateway.Push;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Push;

/// <summary>
/// b05/GW1 W0.6 — the in-gateway direct-to-provider push transport is DELETED, not
/// flag-disabled.
///
/// <para><b>Read this before "simplifying" the assertions.</b> The obvious predicate —
/// "DI resolves exactly 2 <c>IPushTransport</c>" — is <b>invariant under the change</b>
/// and therefore proves nothing: before the delete the disabled branch registered
/// <c>InMemoryPushTransport(Fcm)</c> + <c>(Apns)</c> = 2, and the enabled branch would
/// have registered the real transport + <c>InMemoryPushTransport(Apns)</c> = 2 as well.
/// A count of 2 was true on both sides of the switch.</para>
///
/// <para>So the two legs that actually carry weight are:</para>
/// <list type="number">
///   <item><b>Concrete type + platform.</b> Every resolved <c>IPushTransport</c> must be
///   an <c>InMemoryPushTransport</c>, and the platform set must be exactly
///   { <c>Fcm</c>, <c>Apns</c> } — one each. A count alone cannot see a substitution.</item>
///   <item><b>The type is gone from the assembly.</b> Reflection over the built
///   <c>JeebGateway</c> assembly for <c>JeebGateway.Push.FcmPushTransport</c> must return
///   <c>null</c>. This is the load-bearing leg: <b>no added registration can satisfy it</b>,
///   because it does not ask what is wired — it asks whether the class exists at all.</item>
/// </list>
///
/// <para>The third leg covers the credential surface: the options type must expose no
/// provider credential property and no transport switch, so there is nothing left for a
/// future config to fill in and believe push now leaves the gateway.</para>
/// </summary>
public class PushTransportRegistrationTests
{
    private const string DeletedTransportTypeName = "JeebGateway.Push.FcmPushTransport";

    private static WebApplicationFactory<Program> NewFactory() => new();

    [Fact]
    public void Resolved_PushTransports_Are_Exactly_InMemory_Fcm_And_InMemory_Apns()
    {
        using var factory = NewFactory();

        var transports = factory.Services.GetServices<IPushTransport>().ToArray();

        // Every one is the in-memory double — a substitution would survive a bare count.
        transports.Should().AllBeOfType<InMemoryPushTransport>(
            "the gateway must never register a transport that dials a push provider itself");

        var inMemory = transports.OfType<InMemoryPushTransport>().ToArray();
        inMemory.Should().HaveCount(transports.Length);

        // …and the platform multiset is exactly { Fcm, Apns }: Single() reds on a duplicate
        // as well as on an absence.
        inMemory.Single(t => t.Platform == DevicePlatform.Fcm).Should().NotBeNull();
        inMemory.Single(t => t.Platform == DevicePlatform.Apns).Should().NotBeNull();
        inMemory.Select(t => t.Platform).Should()
            .BeEquivalentTo(new[] { DevicePlatform.Fcm, DevicePlatform.Apns });
    }

    [Fact]
    public void Deleted_Transport_Type_Is_Absent_From_The_Assembly()
    {
        var assembly = typeof(Program).Assembly;

        // POSITIVE CONTROL — the same lookup on a type that IS present must resolve.
        // Without this, a typo in the type name would give a null that looks like a pass.
        assembly.GetType(typeof(InMemoryPushTransport).FullName!)
            .Should().NotBeNull("the reflection lookup must be able to find a type that exists");

        assembly.GetType(DeletedTransportTypeName)
            .Should().BeNull($"{DeletedTransportTypeName} was DELETED by b05/GW1 W0.6; " +
                             "no registration change can make this leg pass again");
    }

    [Fact]
    public void PushOptions_Exposes_No_Provider_Credential_And_No_Transport_Switch()
    {
        var properties = typeof(PushOptions).GetProperties().Select(p => p.Name).ToArray();

        // POSITIVE CONTROL — the surviving, still-used options are still there, so an
        // empty/AWOL property list cannot masquerade as a clean delete.
        properties.Should().Contain("DeliverySla");
        properties.Should().Contain("RetryDelay");
        properties.Should().Contain("TransportTimeout");

        properties.Should().NotContain("UseFcmTransport");
        properties.Should().NotContain("FcmProjectId");
        properties.Should().NotContain("FcmBearerToken");
    }
}
