using FluentAssertions;
using JeebGateway.Services.Cdn;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// JEBV4-259 SECURITY (CWE-22 / CWE-918) — unit tests for the canonicalized-sink SSRF
/// guard (<see cref="CdnUploadUrlResolver.IsOnSignedPutPrefix"/>) that
/// <c>CdnUploadProxyController</c> applies before streaming a KYC upload to cdn-service.
///
/// <para>These tests construct the upstream target EXACTLY as the controller does —
/// <c>new Uri(cdnBase, ToCdnPutSignedPath(routeValue) + query)</c> — where
/// <c>routeValue</c> is the SINGLE-DECODED <c>{**objectPath}</c> Kestrel hands the
/// action. So a PoC "<c>%252e%252e</c>" wire attack is passed here as the literal
/// "<c>%2e%2e</c>" it decodes to, and <see cref="System.Uri"/> then percent-decodes
/// (<c>%2e</c>→<c>.</c>) and collapses the dot-segments — escaping cdn's fixed
/// signed-PUT prefix onto an arbitrary path. Being a pure function, the guard's exact
/// canonicalization is asserted directly, with no dependence on host path-decoding.</para>
/// </summary>
public sealed class CdnUploadProxySsrfGuardTests
{
    private static readonly Uri CdnBase = new("http://cdn-test.internal:10072/");

    /// <summary>
    /// Faithfully reproduce the controller's sink construction. <paramref name="singleDecodedRouteValue"/>
    /// is what Kestrel would hand the action AFTER its single percent-decode.
    /// </summary>
    private static Uri BuildUpstream(string singleDecodedRouteValue, string query = "?exp=1&sig=a") =>
        new(CdnBase, CdnUploadUrlResolver.ToCdnPutSignedPath(singleDecodedRouteValue) + query);

    [Fact]
    public void Legit_ObjectRef_Stays_On_Prefix()
    {
        CdnUploadUrlResolver.IsOnSignedPutPrefix(BuildUpstream("OBJ123"), CdnBase)
            .Should().BeTrue("a normal objectRef resolves under cdn's signed-PUT prefix (guard must not be over-tight)");
    }

    [Fact]
    public void Legit_Nested_ObjectRef_Stays_On_Prefix()
    {
        CdnUploadUrlResolver.IsOnSignedPutPrefix(BuildUpstream("kyc/2026/OBJ123"), CdnBase)
            .Should().BeTrue("a nested objectRef (slashes are legal in the ref) still resolves under the prefix");
    }

    [Theory]
    // PoC-confirmed: "%252e%252e" -> Kestrel single-decode -> "%2e%2e" (below) ->
    // System.Uri decode+collapse -> escapes onto /api/ImageUpload/admin.
    [InlineData("%2e%2e/admin")]
    // Deeper escape onto an unsigned cdn path.
    [InlineData("%2e%2e/%2e%2e/api/ImageUpload/upload")]
    // Backslash separator: System.Uri normalises '\' to '/', then collapses '..'.
    [InlineData("..\\admin")]
    public void Encoded_Or_Backslash_Traversal_That_Escapes_The_Prefix_Is_Rejected(string singleDecodedRouteValue)
    {
        var upstream = BuildUpstream(singleDecodedRouteValue);

        // First prove the escape genuinely happened — the sink IS off-prefix after
        // canonicalization (otherwise the test would be vacuously green).
        upstream.AbsolutePath.Should().NotStartWith(
            "/api/ImageUpload/put-signed/",
            "the traversal must actually escape the prefix for this to be a real regression case");

        // ...and the guard fails closed on it.
        CdnUploadUrlResolver.IsOnSignedPutPrefix(upstream, CdnBase)
            .Should().BeFalse("a target that resolved off the signed-PUT prefix must be rejected");
    }

    [Fact]
    public void Different_Host_Is_Rejected()
    {
        var offHost = new Uri("http://evil.example:10072/api/ImageUpload/put-signed/OBJ1?sig=a");
        CdnUploadUrlResolver.IsOnSignedPutPrefix(offHost, CdnBase)
            .Should().BeFalse("a redirected/foreign host must never be dialed");
    }

    [Fact]
    public void Different_Scheme_Is_Rejected()
    {
        var offScheme = new Uri("https://cdn-test.internal:10072/api/ImageUpload/put-signed/OBJ1?sig=a");
        CdnUploadUrlResolver.IsOnSignedPutPrefix(offScheme, CdnBase)
            .Should().BeFalse("a scheme change off the cdn base is rejected");
    }

    [Fact]
    public void Different_Port_Is_Rejected()
    {
        var offPort = new Uri("http://cdn-test.internal:9999/api/ImageUpload/put-signed/OBJ1?sig=a");
        CdnUploadUrlResolver.IsOnSignedPutPrefix(offPort, CdnBase)
            .Should().BeFalse("a port change off the cdn base is rejected");
    }

    [Fact]
    public void Same_Origin_But_Off_Prefix_Path_Is_Rejected()
    {
        var offPrefix = new Uri(CdnBase, "api/ImageUpload/admin");
        CdnUploadUrlResolver.IsOnSignedPutPrefix(offPrefix, CdnBase)
            .Should().BeFalse("even on cdn's own origin, a path outside the signed-PUT prefix is rejected");
    }
}
