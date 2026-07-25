using System.Net;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Services.Cdn;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// P4/P5 (b01-20260725) — the AUTHENTICATED CDN read proxy
/// (<c>GET /api/cdn/assets/content/{**objectPath}</c>, <c>CdnController.GetAssetContent</c>).
///
/// <para><b>Why this route exists.</b> cdn-service exposes NO signed-download
/// endpoint — <c>CdnController.GetSignedUrl</c> dials <c>api/v1/assets/{id}/signed-url</c>,
/// which is not in cdn's surface at all. The only working read path is cdn's own
/// <c>api/ImageUpload/fetch/{fileName}</c>, and <c>{fileName}</c> is a SINGLE route
/// segment: a nested objectRef must be percent-encoded into one segment (verified
/// live on MSI 2026-07-25 — encoded → 200 + bytes, raw slash → 404). Without this
/// proxy the chat sender sees their own photo (local bytes) and the PEER sees
/// nothing.</para>
///
/// <para>The upstream cdn-service is stubbed via the <c>cdn-proxy</c> named client's
/// primary handler (same technique as <see cref="CdnUploadProxyStreamingTests"/>), so
/// the suite is CI-safe with no live cdn / Docker.</para>
/// </summary>
public sealed class CdnAssetContentProxyTests
{
    private const string ObjectRef = "chat_attachment/abc123.jpg";
    private const string ContentUrl = "/api/cdn/assets/content/chat_attachment/abc123.jpg";

    /// <summary>The exact single-segment-encoded path cdn's fetch route requires.</summary>
    private const string ExpectedUpstreamPath = "/api/ImageUpload/fetch/chat_attachment%2Fabc123.jpg";

    private static readonly byte[] JpegBytes =
        { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

    // ----- TC-A5 — happy path -----

    [Fact]
    public async Task GetAssetContent_Happy_Returns_200_With_Bytes_And_Encodes_ObjectRef_Into_One_Segment()
    {
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = ReadProxyFactory(upstream);
        var client = ClientFor(factory, "p45-read-happy");

        var resp = await client.GetAsync(ContentUrl);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.ToString().Should().Be("image/jpeg");
        (await resp.Content.ReadAsByteArrayAsync()).Should().Equal(JpegBytes);

        // THE load-bearing assertion: the objectRef's '/' must reach cdn
        // PERCENT-ENCODED into a single segment. A raw slash 404s upstream.
        upstream.WasCalled.Should().BeTrue();
        upstream.RequestUri!.AbsolutePath.Should().Be(ExpectedUpstreamPath);
        upstream.RequestUri.Host.Should().Be("cdn-test.internal");
        upstream.Method.Should().Be(HttpMethod.Get);
    }

    // ----- TC-A6 — cdn documents 206 as a fetch success code -----

    [Fact]
    public async Task GetAssetContent_Relays_Bytes_When_Cdn_Answers_206_Not_200()
    {
        // cdn's fetch route documents 206 as its success status (range-capable).
        // The proxy must key on IsSuccessStatusCode, never on == HttpStatusCode.OK,
        // or every real fetch would surface as a 502.
        var upstream = new StubUpstreamHandler(HttpStatusCode.PartialContent, "image/jpeg", JpegBytes);
        using var factory = ReadProxyFactory(upstream);
        var client = ClientFor(factory, "p45-read-206");

        var resp = await client.GetAsync(ContentUrl);

        resp.IsSuccessStatusCode.Should().BeTrue("a 206 from cdn is a successful fetch, not an error");
        (await resp.Content.ReadAsByteArrayAsync()).Should().Equal(JpegBytes);
    }

    // ----- TC-A7 — auth gate -----

    [Fact]
    public async Task GetAssetContent_Without_Identity_Returns_401_And_Never_Dials_Cdn()
    {
        // Unlike the signed PUT (whose HMAC query IS the authz) a plain fetch carries
        // no signature, so the bearer / edge identity is the ONLY gate. This route must
        // never become [PublicEndpoint].
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = ReadProxyFactory(upstream);
        var anon = factory.CreateClient();

        var resp = await anon.GetAsync(ContentUrl);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        upstream.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetAssetContent_Is_Covered_By_The_Class_Level_CdnBroker_Capability()
    {
        // TC-A11 — ADR-005 Layer 2. The action carries NO per-action marker by design:
        // the CLASS-level [RequireCapability(Capabilities.CdnBroker)] on CdnController
        // is Inherited and lands on every action's endpoint metadata, exactly like
        // GetAsset / GetSignedUrl. This asserts the guard's verdict for THIS action by
        // name, so a future refactor that splits the action out of the class (losing the
        // marker) fails here as well as in CapabilityCoverageGuardTests.
        await using var factory = new WebApplicationFactory<Program>();
        using var _ = factory.CreateClient();

        var guard = factory.Services.GetRequiredService<CapabilityCoverageGuard>();
        var uncovered = guard.FindUncoveredActions();

        uncovered.Should().NotContain(n => n.Contains("GetAssetContent"));
        uncovered.Should().BeEmpty("ADR-005 default-deny must stay at zero uncovered actions");
    }

    // ----- TC-A8 — traversal / malformed refs fail closed BEFORE any upstream dial -----

    [Theory]
    // Empty ref (tail-less catch-all).
    [InlineData("/api/cdn/assets/content/")]
    // A literal ".." token embedded in a segment (ASP.NET normalises whole "../"
    // segments out before routing, so this is the shape that actually survives).
    [InlineData("/api/cdn/assets/content/chat_attachment/..evil.jpg")]
    // Single-encoded traversal: Kestrel decodes "%2e%2e" -> ".." into the route value.
    [InlineData("/api/cdn/assets/content/a%2e%2e/b")]
    // Double-encoded traversal: survives Kestrel's SINGLE decode as the literal
    // "%2e%2e" (still carrying '%'), slipping a naive ".." check — System.Uri would
    // then decode + collapse it and escape cdn's fixed fetch prefix.
    [InlineData("/api/cdn/assets/content/%252e%252e/admin")]
    // Backslash — normalises to '/' inside System.Uri.
    [InlineData("/api/cdn/assets/content/OBJ%5cevil")]
    [InlineData("/api/cdn/assets/content/OBJ%5c..%5cadmin")]
    public async Task GetAssetContent_Rejects_Traversal_And_Malformed_Refs_With_400_And_Never_Dials_Cdn(
        string maliciousUrl)
    {
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = ReadProxyFactory(upstream);
        var client = ClientFor(factory, "p45-read-traversal");

        var resp = await client.GetAsync(maliciousUrl);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // THE security invariant: rejected before ANY upstream dial.
        upstream.WasCalled.Should().BeFalse(
            "a malformed/traversing asset ref must fail closed before the gateway dials cdn");
    }

    // ----- TC-A9 — honest upstream status mapping -----

    [Fact]
    public async Task GetAssetContent_Upstream_404_Returns_404()
    {
        var upstream = new StubUpstreamHandler(HttpStatusCode.NotFound, "application/json", Array.Empty<byte>());
        using var factory = ReadProxyFactory(upstream);
        var client = ClientFor(factory, "p45-read-404");

        var resp = await client.GetAsync(ContentUrl);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAssetContent_Upstream_500_Returns_502()
    {
        // Distinct, honest statuses: "the object is gone" (404) must not read the same
        // as "the asset store is broken" (502).
        var upstream = new StubUpstreamHandler(
            HttpStatusCode.InternalServerError, "application/json", Array.Empty<byte>());
        using var factory = ReadProxyFactory(upstream);
        var client = ClientFor(factory, "p45-read-500");

        var resp = await client.GetAsync(ContentUrl);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ----- TC-A10 — kill switch -----

    [Fact]
    public async Task GetAssetContent_Flag_Off_Returns_503_And_Never_Dials_Cdn()
    {
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = ReadProxyFactory(upstream, cdnFlagOn: false);
        var client = ClientFor(factory, "p45-read-flagoff");

        var resp = await client.GetAsync(ContentUrl);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        upstream.WasCalled.Should().BeFalse();
    }

    // ----- TC-A12 — route precedence sanity -----

    [Fact]
    public async Task GetAssetContent_Tail_Less_Content_Path_Is_Harmless_And_Never_500s()
    {
        // "content" is a literal segment and outranks the {assetId} parameter, so a
        // tail-less GET /api/cdn/assets/content binds this catch-all with an empty
        // objectPath (400). Should routing ever prefer {assetId}="content" instead, the
        // metadata action answers 404 for an unknown asset. Either outcome is harmless;
        // the invariant pinned here is "never a 500, and cdn is never dialled".
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = ReadProxyFactory(upstream);
        var client = ClientFor(factory, "p45-read-precedence");

        var resp = await client.GetAsync("/api/cdn/assets/content");

        ((int)resp.StatusCode).Should().NotBe(500);
        resp.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
        upstream.WasCalled.Should().BeFalse();
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> ReadProxyFactory(
        StubUpstreamHandler handler, bool cdnFlagOn = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Cdn", cdnFlagOn ? "true" : "false");
            builder.UseSetting("Services:Cdn:BaseUrl", "http://cdn-test.internal:10072/");
            builder.ConfigureServices(services =>
            {
                // Swap ONLY the primary handler of the dedicated cdn-proxy client so the
                // read is captured instead of hitting a real cdn.
                services.AddHttpClient(CdnUploadUrlResolver.ProxyHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);

                // The metadata/signed-url actions on the same controller must never dial
                // a real cdn if route precedence sends a request their way (TC-A12).
                services.RemoveAll<ICDNServiceClient>();
                services.AddSingleton<ICDNServiceClient>(new NullCdnClient());
            });
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        // Opaque UM role "customer" == contract role "client" (JeebRoleTranslator);
        // CdnBroker is granted to Participant = {client, jeeber}.
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    /// <summary>
    /// Stands in for cdn-service on the <c>cdn-proxy</c> client. Records whether it was
    /// dialled at all — the fail-closed assertions depend on that being observable.
    /// </summary>
    private sealed class StubUpstreamHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _contentType;
        private readonly byte[] _body;

        public StubUpstreamHandler(HttpStatusCode status, string contentType, byte[] body)
        {
            _status = status;
            _contentType = contentType;
            _body = body;
        }

        public bool WasCalled { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Method = request.Method;
            RequestUri = request.RequestUri;

            var content = new ByteArrayContent(_body);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);

            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }

    /// <summary>
    /// A cdn client that answers "no such asset" and refuses the unused actions —
    /// keeps the metadata/signed-url routes off the network in these tests.
    /// </summary>
    private sealed class NullCdnClient : ICDNServiceClient
    {
        public Task<CdnUploadTicket> MintUploadUrlAsync(CdnUploadUrlRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnAsset> UploadAsync(CdnUploadRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnSignedUrl> GetSignedUrlAsync(string assetId, int ttlSeconds, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnAsset?> GetAssetAsync(string assetId, CancellationToken ct)
            => Task.FromResult<CdnAsset?>(null);
    }
}
