using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using JeebGateway.Push;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw1Pack;

/// <summary>
/// GW1 TEST PACK — member item <b>W0.6</b>: the in-gateway direct-to-provider push
/// transport is DELETED, not flag-disabled.
///
/// <para>Run this item alone:
/// <c>dotnet test --no-build --filter "FullyQualifiedName~Gw1Pack.W06_"</c>.
/// Every assertion here is scoped to W0.6, so a red in this class names the item —
/// which is the whole point of splitting the pack by member item.</para>
///
/// <para><b>What the pack adds over the writer's own
/// <c>PushTransportRegistrationTests</c>, which it does not replace.</b> That file
/// asks whether one named type — <c>JeebGateway.Push.FcmPushTransport</c> — still
/// exists. That is a good question and it is asked again here, but it is answered by
/// a <i>string</i>: reinstating the same code under any other name
/// (<c>FcmPushTransportV2</c>, <c>GooglePushTransport</c>, <c>HttpPushTransport</c>)
/// leaves it green. The assertions below are shape-based instead:</para>
/// <list type="bullet">
///   <item><b>GW1-A5</b> — the set of <see cref="IPushTransport"/> implementations in
///   the whole assembly, by scan. A renamed re-add reds.</item>
///   <item><b>GW1-A6</b> — no push type takes an <see cref="HttpClient"/> or
///   <see cref="IHttpClientFactory"/> constructor argument, i.e. nothing in the push
///   module can dial anything. Carries an in-test positive control proving the scan
///   can detect an HttpClient constructor when one is present.</item>
///   <item><b>GW1-A8</b> — <see cref="PushOptions"/> exposes no credential-shaped and
///   no transport-switch property, matched by PATTERN, not by the three old names.</item>
///   <item><b>GW1-A9</b> — the <i>live bound configuration</i> of a booted host has no
///   such key either. A9 is the one that would catch the keys surviving in an
///   environment-variable or Production-overlay layer that the type-level check
///   cannot see.</item>
/// </list>
///
/// <para><b>Out of scope here, deliberately.</b> Whether a push actually reaches a
/// handset is a <c>device</c>-class claim and no host test can stand in for it —
/// <see cref="InMemoryPushTransport"/> enqueues to a <c>ConcurrentQueue</c> and
/// returns, and the send is then counted Delivered. Nothing below should be read as
/// evidence that push works; the claim under test is only that the gateway no longer
/// contains a path that speaks to a push provider itself.</para>
/// </summary>
public class W06_FcmTransportDeletedTests
{
    private const string DeletedTypeName = "JeebGateway.Push.FcmPushTransport";

    private static IEnumerable<Type> AssemblyTypes()
    {
        var assembly = typeof(Program).Assembly;
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    // ── GW1-A5 — the implementation SET, not one name ──────────────────────

    [Fact]
    public void A5_The_Only_IPushTransport_Implementation_In_The_Assembly_Is_The_InMemory_Double()
    {
        var implementations = AssemblyTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPushTransport).IsAssignableFrom(t))
            .OrderBy(t => t.FullName)
            .ToArray();

        // POSITIVE CONTROL — the scan must find something. An assembly scan that
        // silently returned nothing would satisfy "no forbidden transport" vacuously
        // and look identical to a clean delete.
        implementations.Should().NotBeEmpty(
            "the scan must be able to see IPushTransport implementations at all");

        implementations.Select(t => t.FullName).Should().BeEquivalentTo(
            new[] { typeof(InMemoryPushTransport).FullName },
            "W0.6 deletes the in-gateway provider transport outright; a re-add under ANY " +
            "name — not just the old one — must red here");

        // Kept as well, because it is the batch's own contract wording and it is the
        // one leg no added registration can satisfy.
        typeof(Program).Assembly.GetType(DeletedTypeName).Should().BeNull();
    }

    // ── GW1-A6 — nothing in the push module can dial ───────────────────────

    [Fact]
    public void A6_No_Push_Type_Takes_An_HttpClient_Or_HttpClientFactory_Constructor_Argument()
    {
        static bool DialsOverHttp(Type t) => t.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(HttpClient)
                   || p.ParameterType == typeof(IHttpClientFactory));

        var pushTypes = AssemblyTypes()
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith("JeebGateway.Push", StringComparison.Ordinal))
            .ToArray();

        pushTypes.Should().NotBeEmpty("the namespace scan must find the push module");

        // POSITIVE CONTROL for the predicate itself. If DialsOverHttp could never
        // return true, the assertion below would be a tautology. KycServiceClient is a
        // real, unrelated type in this assembly whose constructor takes an HttpClient.
        var knownDialer = AssemblyTypes().Single(t => t.FullName == "JeebGateway.Services.Clients.KycServiceClient");
        DialsOverHttp(knownDialer).Should().BeTrue(
            "the constructor scan must be able to DETECT an HttpClient dependency, " +
            "or its absence in the push module proves nothing");

        pushTypes.Where(DialsOverHttp).Should().BeEmpty(
            "owner ruling: the gateway must never speak to a push provider itself — " +
            "every push leaves via the push microservice");
    }

    // ── GW1-A7 — the DI smoke the batch's Test-pack section names ──────────

    [Fact]
    public void A7_Resolved_Transports_Are_Exactly_InMemory_Fcm_Plus_InMemory_Apns()
    {
        using var factory = new WebApplicationFactory<Program>();

        var transports = factory.Services.GetServices<IPushTransport>().ToArray();

        transports.Should().NotBeEmpty();
        transports.Should().AllBeOfType<InMemoryPushTransport>();
        transports.OfType<InMemoryPushTransport>().Select(t => t.Platform)
            .Should().BeEquivalentTo(new[] { DevicePlatform.Fcm, DevicePlatform.Apns },
                "a bare count of 2 was true on BOTH sides of the old switch and proves nothing; " +
                "the concrete type and the platform multiset are what changed");

        // DevicePlatform.Fcm is the device-token DISCRIMINATOR and must survive the
        // delete. Over-deleting it would break routing while leaving every "is the
        // transport gone?" assertion green.
        Enum.IsDefined(typeof(DevicePlatform), DevicePlatform.Fcm).Should().BeTrue();
    }

    // ── GW1-A8 — the credential surface, matched by pattern ────────────────

    [Fact]
    public void A8_PushOptions_Has_No_Credential_Shaped_Property_And_No_Transport_Switch()
    {
        var props = typeof(PushOptions).GetProperties().Select(p => p.Name).ToArray();

        // POSITIVE CONTROL — the surviving options are still bound, so an emptied type
        // cannot masquerade as a clean delete.
        props.Should().HaveCountGreaterThan(2);
        props.Should().Contain(new[] { "DeliverySla", "RetryDelay", "TransportTimeout" });

        props.Where(n => n.StartsWith("Fcm", StringComparison.Ordinal)).Should().BeEmpty(
            "a credential-shaped property with no consumer is the landmine W0.6 removes");
        props.Where(n => n.StartsWith("Use", StringComparison.Ordinal)
                      && n.EndsWith("Transport", StringComparison.Ordinal)).Should().BeEmpty(
            "there must be no switch left to flip");
        typeof(PushOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(bool)).Should().BeEmpty(
            "PushOptions carries no boolean feature switch after W0.6");
    }

    // ── GW1-A9 — the LIVE bound configuration, not just the type ───────────

    [Fact]
    public void A9_The_Booted_Hosts_Push_Configuration_Section_Has_No_Fcm_Or_Transport_Switch_Key()
    {
        using var factory = new WebApplicationFactory<Program>();
        var config = factory.Services.GetRequiredService<IConfiguration>();

        var keys = config.GetSection(PushOptions.SectionName).GetChildren()
            .Select(c => c.Key)
            .Where(k => !k.StartsWith("_comment", StringComparison.Ordinal))
            .ToArray();

        // POSITIVE CONTROL — the section must actually be present in the bound config.
        // Reading an ABSENT section returns an empty child list, which would satisfy
        // every "no forbidden key" assertion below without proving anything.
        keys.Should().NotBeEmpty($"the '{PushOptions.SectionName}' section must be bound for this check to mean anything");

        keys.Should().NotContain(new[] { "UseFcmTransport", "FcmProjectId", "FcmBearerToken" });
        keys.Where(k => k.StartsWith("Fcm", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
    }
}
