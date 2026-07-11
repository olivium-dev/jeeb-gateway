using FluentAssertions;
using JeebGateway.Services.Cdn;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// JEBV4-259 — unit tests for the pure URL-resolution logic behind the KYC-photo
/// upload fix (approach B). Pins the two approach-INDEPENDENT decisions:
/// (1) a relative / internal cdn upload_url is rewritten to the ABSOLUTE gateway
/// streaming-proxy route (the actual production bug), and (2) an already-public
/// absolute URL is passed through untouched (forward-compatible with S3 / approach A).
/// </summary>
public sealed class CdnUploadUrlResolverTests
{
    private static readonly Uri CdnInternalBase = new("http://192.168.2.50:10072/");
    private const string GatewayPublicBase = "https://jeeb.fds-1.com";

    [Fact]
    public void Relative_HostLess_UploadUrl_Is_Rewritten_To_Absolute_Gateway_Proxy_Preserving_Query()
    {
        // The current Local-provider shape (PresignedPutSigner.cs).
        const string relative = "/api/ImageUpload/put-signed/OBJ123?exp=1720000000&ct=image/jpeg&sig=abc";

        var resolved = CdnUploadUrlResolver.Resolve(relative, CdnInternalBase, GatewayPublicBase);

        resolved.Should().Be(
            "https://jeeb.fds-1.com/api/cdn/put-signed/OBJ123?exp=1720000000&ct=image/jpeg&sig=abc");
    }

    [Fact]
    public void Absolute_But_Internal_Cdn_Host_Is_Also_Proxied()
    {
        // A provider that returns an absolute URL at the internal-only cdn host is
        // still unreachable by the mobile client → proxy it.
        const string internalAbsolute =
            "http://192.168.2.50:10072/api/ImageUpload/put-signed/OBJ777?exp=9&ct=image/png&sig=zzz";

        var resolved = CdnUploadUrlResolver.Resolve(internalAbsolute, CdnInternalBase, GatewayPublicBase);

        resolved.Should().Be(
            "https://jeeb.fds-1.com/api/cdn/put-signed/OBJ777?exp=9&ct=image/png&sig=zzz");
    }

    [Fact]
    public void Public_Absolute_UploadUrl_Is_Passed_Through_Untouched()
    {
        // A genuinely public pre-signed URL (future S3 / approach A) is reachable
        // directly — do NOT route the bytes through the gateway.
        const string s3 = "https://s3.eu.amazonaws.com/jeeb-kyc/OBJ9?X-Amz-Signature=deadbeef";

        var resolved = CdnUploadUrlResolver.Resolve(s3, CdnInternalBase, GatewayPublicBase);

        resolved.Should().Be(s3);
    }

    [Fact]
    public void ObjectRef_With_Slashes_Survives_The_RoundTrip()
    {
        const string relative = "/api/ImageUpload/put-signed/kyc/2026/OBJ123?sig=abc";

        var resolved = CdnUploadUrlResolver.Resolve(relative, CdnInternalBase, GatewayPublicBase);

        resolved.Should().Be("https://jeeb.fds-1.com/api/cdn/put-signed/kyc/2026/OBJ123?sig=abc");
        // The reverse map reconstructs cdn's exact signed-PUT path.
        CdnUploadUrlResolver.ToCdnPutSignedPath("kyc/2026/OBJ123")
            .Should().Be("api/ImageUpload/put-signed/kyc/2026/OBJ123");
    }

    [Fact]
    public void Empty_UploadUrl_Throws()
    {
        var act = () => CdnUploadUrlResolver.Resolve("", CdnInternalBase, GatewayPublicBase);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Relative_Url_That_Is_Not_The_Signed_Put_Path_Throws_Rather_Than_Mint_Unreachable()
    {
        // A relative URL the gateway cannot map to the known signed-PUT route must
        // fail LOUD (broker → 502), never silently hand the client a dead URL.
        var act = () => CdnUploadUrlResolver.Resolve("/some/other/path?x=1", CdnInternalBase, GatewayPublicBase);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Trailing_Slash_On_Gateway_Base_Does_Not_Double_Up()
    {
        var resolved = CdnUploadUrlResolver.Resolve(
            "/api/ImageUpload/put-signed/OBJ1?sig=a", CdnInternalBase, "https://jeeb.fds-1.com/");

        resolved.Should().Be("https://jeeb.fds-1.com/api/cdn/put-signed/OBJ1?sig=a");
    }
}
