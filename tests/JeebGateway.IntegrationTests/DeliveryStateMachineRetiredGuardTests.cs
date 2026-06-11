using FluentAssertions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1479 boundary grep-guard (the corrected, narrowed AC2).
///
/// The CTO correction scoped the retirement to EXACTLY the legacy linear
/// delivery state machine and its delivery-transition code path — NOT the whole
/// in-gateway store. This guard scans the gateway source tree and asserts:
///
/// <list type="bullet">
///   <item><b>0 hits for <c>DeliveryStateMachine</c></b> anywhere under
///     <c>src/JeebGateway</c> (the legacy linear-enum class and every reference
///     to it, comments included, are gone);</item>
///   <item>the <c>DeliveryStateMachine.cs</c> and <c>DeliveryTransitionResult.cs</c>
///     source files no longer exist;</item>
///   <item>the delivery-transition path no longer calls the local store: the
///     PATCH /deliveries/{id}/status controller does NOT invoke
///     <c>_store.TryTransition*</c>, and nothing in src declares/calls a
///     <c>TryTransitionAsync(</c> store method.</item>
/// </list>
///
/// Crucially this guard does NOT forbid <see cref="JeebGateway.Requests.DeliverySm"/>
/// (the owner-approved canonical SM), nor <c>IRequestsStore</c> /
/// <c>InMemoryRequestsStore</c> as a whole — those keep backing Requests, Offers,
/// RequestOffers, Disputes, Ratings, Settlement, Cancellation and OtpHandover.
/// </summary>
public class DeliveryStateMachineRetiredGuardTests
{
    [Fact]
    public void LegacyDeliveryStateMachine_Has_Zero_Hits_In_Gateway_Source()
    {
        var srcRoot = LocateGatewaySrc();
        srcRoot.Should().NotBeNull(
            "the gateway src/JeebGateway tree must be locatable from the test bin dir");

        var csFiles = Directory.EnumerateFiles(srcRoot!, "*.cs", SearchOption.AllDirectories).ToList();
        csFiles.Should().NotBeEmpty("the gateway source tree should contain .cs files");

        // (1) The retired source files are gone.
        csFiles.Should().NotContain(
            f => Path.GetFileName(f) == "DeliveryStateMachine.cs",
            "DeliveryStateMachine.cs must be deleted (JEB-1479)");
        csFiles.Should().NotContain(
            f => Path.GetFileName(f) == "DeliveryTransitionResult.cs",
            "DeliveryTransitionResult.cs (the retired transition outcome bundle) must be deleted (JEB-1479)");

        // (2) Zero textual hits for the legacy class name — comments included.
        var dsmHits = csFiles
            .Where(f => File.ReadAllText(f).Contains("DeliveryStateMachine", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();
        dsmHits.Should().BeEmpty(
            "JEB-1479 AC2: there must be 0 hits for 'DeliveryStateMachine' in gateway source");

        // (3) The delivery-transition path no longer calls the local store.
        var transitionStoreCalls = csFiles
            .Where(f => File.ReadAllText(f).Contains("TryTransitionAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();
        transitionStoreCalls.Should().BeEmpty(
            "JEB-1479 AC2: the local-store delivery-transition method must be gone (no TryTransitionAsync call sites or declarations remain)");

        var controller = csFiles.SingleOrDefault(f => Path.GetFileName(f) == "DeliveriesController.cs");
        controller.Should().NotBeNull("DeliveriesController.cs must still exist (the route stays alive as a deprecated alias)");
        var controllerSrc = File.ReadAllText(controller!);
        controllerSrc.Should().NotContain("_store.TryTransition",
            "the delivery-transition route must forward to delivery-service, never the local store");
        controllerSrc.Should().Contain("PatchStatusViaDeliveryServiceAsync",
            "PATCH /deliveries/{id}/status must forward to delivery-service's canonical transition contract");
    }

    [Fact]
    public void Store_Retirement_Is_Narrow_RequestsStore_Survives()
    {
        var srcRoot = LocateGatewaySrc();
        srcRoot.Should().NotBeNull();

        // The narrowed scope keeps the store backing the non-delivery domains.
        File.Exists(Path.Combine(srcRoot!, "Requests", "IRequestsStore.cs"))
            .Should().BeTrue("IRequestsStore must NOT be deleted (it backs Requests/Offers/Disputes/etc.)");
        File.Exists(Path.Combine(srcRoot!, "Requests", "InMemoryRequestsStore.cs"))
            .Should().BeTrue("InMemoryRequestsStore must NOT be deleted wholesale (narrowed AC2)");
        File.Exists(Path.Combine(srcRoot!, "Requests", "DeliverySm.cs"))
            .Should().BeTrue("the owner-approved canonical DeliverySm must remain");
    }

    /// <summary>
    /// Walks up from the test assembly's base dir looking for the gateway
    /// <c>src/JeebGateway</c> source tree. Mirrors the locator used by
    /// DeliverySmParityTests so it works in both the isolated checkout and CI.
    /// </summary>
    private static string? LocateGatewaySrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Controllers", "DeliveriesController.cs")))
            {
                return candidate;
            }
        }
        return null;
    }
}
