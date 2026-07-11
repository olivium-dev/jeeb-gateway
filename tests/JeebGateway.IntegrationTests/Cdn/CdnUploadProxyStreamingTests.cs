using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Cdn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// JEBV4-259 (approach B) — the KYC-photo streaming upload proxy
/// (<c>PUT /api/cdn/put-signed/{**objectPath}</c>, CdnUploadProxyController).
///
/// <para>These are the regression tests for the 415 wall: they PUT raw image bytes
/// at the gateway proxy and prove the request now returns <b>2xx (not 415)</b>, that
/// the gateway STREAMS the body to cdn-service's signed-PUT endpoint with the
/// <b>method preserved (PUT)</b>, the <b>Content-Type preserved</b>, the
/// <b>bytes intact</b>, and the <b>signature-bearing query string forwarded
/// verbatim</b> — with NO Authorization header (the signed URL is bearer-free).
/// The upstream cdn-service is stubbed via the <c>cdn-proxy</c> named client's
/// primary handler, so the suite is CI-safe with no live cdn / Docker.</para>
/// </summary>
public sealed class CdnUploadProxyStreamingTests
{
    private static readonly byte[] JpegBytes =
        { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x01, 0x02, 0x03, 0x7F, 0x00 };

    [Fact]
    public async Task Put_Signed_Streams_Bytes_To_Cdn_And_Returns_2xx_Not_415()
    {
        var capture = new CapturingHandler(HttpStatusCode.OK, """{"ok":true}""");
        using var factory = ProxyFactory(capture);

        // No X-User-Id / bearer — the signed-PUT route is [AllowAnonymous] on purpose.
        var client = factory.CreateClient();

        var content = new ByteArrayContent(JpegBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/cdn/put-signed/OBJ123?exp=1720000000&sig=abc123&ct=image%2Fjpeg")
        {
            Content = content,
        };

        var response = await client.SendAsync(request);

        // The whole point of the ticket: no more 415, a real 2xx.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)response.StatusCode).Should().NotBe(415);

        // Method preserved.
        capture.Method.Should().Be(HttpMethod.Put);
        // Path mapped 1:1 to cdn-service's signed-PUT endpoint.
        capture.RequestUri!.AbsolutePath.Should().Be("/api/ImageUpload/put-signed/OBJ123");
        // Signature-bearing query forwarded VERBATIM (so cdn's HMAC validates).
        capture.RequestUri.Query.Should().Contain("exp=1720000000");
        capture.RequestUri.Query.Should().Contain("sig=abc123");
        capture.RequestUri.Query.Should().Contain("ct=image%2Fjpeg");
        // Content-Type preserved (not application/json).
        capture.ContentType.Should().Be("image/jpeg");
        // Bytes streamed through intact.
        capture.Body.Should().Equal(JpegBytes);
        // Bearer-free: nothing forwarded upstream.
        capture.HadAuthorizationHeader.Should().BeFalse();
    }

    [Fact]
    public async Task Put_Signed_Allows_Photo_Larger_Than_Global_Json_Body_Cap()
    {
        // JEBV4-259 — the global RequestValidationMiddleware caps bodies at 1 MB
        // (tuned for JSON). A real KYC photo exceeds that; the upload proxy path is
        // exempt (it self-limits to 15 MB). Prove a ~1.5 MB PUT is NOT rejected 413.
        var capture = new CapturingHandler(HttpStatusCode.OK, "{}");
        using var factory = ProxyFactory(capture);
        var client = factory.CreateClient();

        var bigPhoto = new byte[1_500_000];
        new Random(1).NextBytes(bigPhoto);
        var content = new ByteArrayContent(bigPhoto);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/cdn/put-signed/OBJ-big?sig=ok")
        {
            Content = content,
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ((int)response.StatusCode).Should().NotBe(413);
        capture.Body.Length.Should().Be(bigPhoto.Length);
    }

    [Fact]
    public async Task Put_Signed_Relays_Upstream_NonSuccess_Verbatim()
    {
        // A dumb pipe: whatever cdn decides (here a 403 bad-signature) is relayed —
        // proving the gateway itself is not the 415/blocker; cdn is record-of-truth.
        var capture = new CapturingHandler(HttpStatusCode.Forbidden, """{"error":"bad_signature"}""");
        using var factory = ProxyFactory(capture);
        var client = factory.CreateClient();

        var content = new ByteArrayContent(JpegBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/cdn/put-signed/OBJ9?exp=1&sig=nope")
        {
            Content = content,
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("bad_signature");
        capture.Method.Should().Be(HttpMethod.Put); // it did reach the upstream
    }

    [Fact]
    public async Task Put_Signed_Rejects_DotDot_In_ObjectRef_With_400_And_Never_Dials_Cdn()
    {
        // SSRF guard: a ".." in the objectRef could otherwise redirect the proxied
        // PUT off cdn's fixed signed-PUT prefix. (ASP.NET normalises "../" dot
        // SEGMENTS out of the path before routing; this asserts the belt-and-braces
        // guard for a ".." that survives as an embedded token in a single segment.)
        var capture = new CapturingHandler(HttpStatusCode.OK, "{}");
        using var factory = ProxyFactory(capture);
        var client = factory.CreateClient();

        var content = new ByteArrayContent(JpegBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/cdn/put-signed/OBJ..evil?sig=x")
        {
            Content = content,
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        capture.WasCalled.Should().BeFalse("a ../-bearing objectRef must be rejected before any upstream dial");
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> ProxyFactory(CapturingHandler handler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Services:Cdn:BaseUrl", "http://cdn-test.internal:10072/");
            builder.ConfigureServices(services =>
            {
                // Swap ONLY the primary handler of the dedicated cdn-proxy client so
                // the streamed PUT is captured instead of hitting a real cdn.
                services.AddHttpClient(CdnUploadUrlResolver.ProxyHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public bool WasCalled { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ContentType { get; private set; }
        public byte[] Body { get; private set; } = Array.Empty<byte>();
        public bool HadAuthorizationHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            Method = request.Method;
            RequestUri = request.RequestUri;
            HadAuthorizationHeader = request.Headers.Authorization is not null;
            if (request.Content is not null)
            {
                ContentType = request.Content.Headers.ContentType?.ToString();
                // Draining the content pulls the gateway's StreamContent over Request.Body.
                Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
