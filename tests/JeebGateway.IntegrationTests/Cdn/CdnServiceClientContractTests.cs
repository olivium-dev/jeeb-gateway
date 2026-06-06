using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// CONTRACT TEST for the gateway↔cdn-service seam (thin-BFF wire, behind
/// <c>FeatureFlags:UseUpstream:Cdn</c>). Serves JEB-527 / JEB-519 / JEB-59.
///
/// <para>
/// cdn-service is NOT yet deployed, so there is no live box to hit — these are
/// CI-SAFE binding tests that drive the PRODUCTION <see cref="CDNServiceClient"/>
/// over a stub <see cref="HttpMessageHandler"/> returning the camelCase JSON the
/// service is expected to emit. They pin the request shape (multipart upload,
/// signed-url query, asset metadata read) and the response binding so the
/// gateway side is locked down before the upstream ships. When cdn-service is
/// deployed, add a JEEB_CDN_LIVE-gated live test mirroring
/// <c>FeedbackServiceClientRealWireTests</c>.
/// </para>
/// </summary>
public sealed class CdnServiceClientContractTests
{
    private const string CdnBaseUrl = "http://192.168.2.50:10099"; // stand-in; real port TBD

    [Fact]
    public async Task UploadAsync_Posts_Multipart_To_Assets_And_Binds_CdnAsset()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Json("""
            {
              "assetId": "aa11bb22-cc33-dd44-ee55-ff6677889900",
              "fileName": "tos-signed.pdf",
              "contentType": "application/pdf",
              "sizeBytes": 4,
              "category": "tos-signed",
              "ownerUserId": "user-1",
              "createdAt": "2026-06-02T10:00:00Z",
              "expiresAt": "2026-08-31T10:00:00Z"
            }
            """);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(CdnBaseUrl + "/") };
        var sut = new CDNServiceClient(http);

        var asset = await sut.UploadAsync(new CdnUploadRequest
        {
            Content = Encoding.UTF8.GetBytes("%PDF"),
            FileName = "tos-signed.pdf",
            ContentType = "application/pdf",
            Category = "tos-signed",
            OwnerUserId = "user-1",
            RetentionDays = 90,
        }, ct: default);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/assets");
        captured.Content.Should().BeOfType<MultipartFormDataContent>();
        capturedBody.Should().Contain("tos-signed.pdf");
        capturedBody.Should().Contain("retentionDays");
        capturedBody.Should().Contain("90");
        capturedBody.Should().Contain("ownerUserId");

        asset.AssetId.Should().Be("aa11bb22-cc33-dd44-ee55-ff6677889900");
        asset.ContentType.Should().Be("application/pdf");
        asset.Category.Should().Be("tos-signed");
        asset.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-08-31T10:00:00Z"));
    }

    [Fact]
    public async Task GetSignedUrlAsync_Gets_SignedUrl_With_Ttl_Query_And_Binds()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            return Json("""
            {
              "url": "https://cdn.jeeb.lb/d/aa11bb22?sig=abc&exp=123",
              "expiresAt": "2026-06-02T10:05:00Z"
            }
            """);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(CdnBaseUrl + "/") };
        var sut = new CDNServiceClient(http);

        var signed = await sut.GetSignedUrlAsync("aa11bb22", ttlSeconds: 300, ct: default);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/assets/aa11bb22/signed-url");
        captured.RequestUri.Query.Should().Contain("ttlSeconds=300");

        signed.Url.Should().StartWith("https://cdn.jeeb.lb/d/");
        signed.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-06-02T10:05:00Z"));
    }

    [Fact]
    public async Task MintUploadUrlAsync_Posts_To_UploadUrl_And_Binds_Ticket()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Json("""
            {
              "uploadUrl": "https://cdn.jeeb.lb/put/id_document_front?sig=abc",
              "objectRef": "cdn://obj/id_document_front/aa11",
              "expiresIn": 300
            }
            """);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(CdnBaseUrl + "/") };
        var sut = new CDNServiceClient(http);

        var ticket = await sut.MintUploadUrlAsync(new CdnUploadUrlRequest
        {
            Slot = "id_document_front",
            ContentType = "image/jpeg",
            OwnerUserId = "user-1",
            TtlSeconds = 300,
        }, ct: default);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/v1/assets/upload-url");
        capturedBody.Should().Contain("id_document_front");
        capturedBody.Should().Contain("ownerUserId");

        ticket.UploadUrl.Should().StartWith("https://cdn.jeeb.lb/put/");
        ticket.ObjectRef.Should().Be("cdn://obj/id_document_front/aa11");
        ticket.ExpiresInSeconds.Should().Be(300);
    }

    [Fact]
    public async Task GetAssetAsync_Returns_Null_On_404_Retention_Expiry()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler) { BaseAddress = new Uri(CdnBaseUrl + "/") };
        var sut = new CDNServiceClient(http);

        var asset = await sut.GetAssetAsync("expired-asset", ct: default);

        asset.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_Throws_On_Non2xx()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler) { BaseAddress = new Uri(CdnBaseUrl + "/") };
        var sut = new CDNServiceClient(http);

        var act = async () => await sut.UploadAsync(new CdnUploadRequest
        {
            Content = Encoding.UTF8.GetBytes("%PDF"),
            FileName = "x.pdf",
            ContentType = "application/pdf",
            Category = "tos-signed",
            OwnerUserId = "user-1",
        }, ct: default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }
}
