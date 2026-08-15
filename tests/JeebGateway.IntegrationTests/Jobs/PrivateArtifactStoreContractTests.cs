using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using JeebGateway.Artifacts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class PrivateArtifactStoreContractTests
{
    [Fact]
    public async Task Put_Uses_Bearer_Multipart_And_Stable_Idempotency_Contract()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.Created,
            """{"artifactRef":"opaque/ref","expiresAt":"2099-08-17T12:00:00Z","sizeBytes":3}"""));
        var store = Client(handler, secret.Path);
        var expiry = DateTimeOffset.Parse("2099-08-17T12:00:00Z");

        var artifact = await store.PutAsync(
            "data-export:stable",
            "sha256:owner",
            "export.json",
            "application/json",
            [1, 2, 3],
            expiry,
            CancellationToken.None);

        artifact.Should().Be(new PrivateArtifact("opaque/ref", expiry, 3));
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.PathAndQuery.Should().Be("/v1/private-artifacts");
        request.Authorization.Should().Be($"Bearer {secret.Value}");
        request.IdempotencyKey.Should().Be("data-export:stable");
        request.ContentType.Should().StartWith("multipart/form-data; boundary=");
        request.Body.Should().Contain("name=ownerRef");
        request.Body.Should().Contain("sha256:owner");
        request.Body.Should().Contain("name=expiresAt");
        request.Body.Should().Contain("2099-08-17T12:00:00.0000000+00:00");
        request.Body.Should().Contain("name=file; filename=export.json");
        request.Body.Should().Contain("Content-Type: application/json");
    }

    [Fact]
    public async Task RecoverUpload_Uses_Bearer_And_Idempotency_Key_Without_Content()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """{"artifactRef":"opaque/ref","expiresAt":"2099-08-17T12:00:00Z","sizeBytes":3}"""));
        var store = Client(handler, secret.Path);

        var artifact = await store.RecoverUploadAsync(
            "data-export:stable",
            CancellationToken.None);

        artifact.Should().Be(new PrivateArtifact(
            "opaque/ref",
            DateTimeOffset.Parse("2099-08-17T12:00:00Z"),
            3));
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Get);
        request.PathAndQuery.Should().Be(
            "/v1/private-artifacts/by-idempotency-key");
        request.Authorization.Should().Be($"Bearer {secret.Value}");
        request.IdempotencyKey.Should().Be("data-export:stable");
        request.ContentType.Should().BeNull();
        request.Body.Should().BeNull();
    }

    [Fact]
    public async Task RecoverUpload_Returns_Null_Only_For_Owner_404()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var store = Client(handler, secret.Path);

        var artifact = await store.RecoverUploadAsync(
            "data-export:unknown",
            CancellationToken.None);

        artifact.Should().BeNull();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RecoverUpload_Does_Not_Treat_Conflict_As_Not_Found()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.Conflict,
            """{"error":"upload still resolving"}"""));
        var store = Client(handler, secret.Path);

        var act = () => store.RecoverUploadAsync(
            "data-export:in-progress",
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(error => error.StatusCode == HttpStatusCode.Conflict);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Download_Uses_Escaped_Reference_SingleUse_And_Bounded_Expiry()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """{"url":"https://downloads.example.test/private/one","expiresAt":"2099-08-17T12:00:00Z"}"""));
        var store = Client(handler, secret.Path);

        var download = await store.CreateDownloadUrlAsync(
            "opaque/ref with space",
            TimeSpan.FromHours(1),
            singleUse: true,
            CancellationToken.None);

        download.Url.Should().Be("https://downloads.example.test/private/one");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.PathAndQuery.Should().Be(
            "/v1/private-artifacts/opaque%2Fref%20with%20space/download-url");
        request.Authorization.Should().Be($"Bearer {secret.Value}");
        request.Body.Should().Be("{\"expiresInSeconds\":300,\"singleUse\":true}");
    }

    [Fact]
    public async Task Delete_Treats_Owner_404_As_Idempotent_Success()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var store = Client(handler, secret.Path);

        await store.DeleteAsync("opaque/ref", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].PathAndQuery.Should().Be(
            "/v1/private-artifacts/opaque%2Fref");
    }

    [Fact]
    public async Task Missing_Secret_Fails_Closed_Before_Any_Owner_Request()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var store = Client(handler, tokenFile: string.Empty);

        var act = () => store.PutAsync(
            "stable",
            "owner",
            "export.json",
            "application/json",
            [1],
            DateTimeOffset.Parse("2099-08-17T12:00:00Z"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*BEARER_TOKEN_FILE*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NonHttps_Download_Url_Is_Rejected()
    {
        using var secret = TempSecret.Create("artifact-owner-token-0123456789abcdef0123456789abcdef");
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """{"url":"http://internal-owner/private/one","expiresAt":"2099-08-17T12:00:00Z"}"""));
        var store = Client(handler, secret.Path);

        var act = () => store.CreateDownloadUrlAsync(
            "opaque",
            TimeSpan.FromMinutes(1),
            singleUse: true,
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*invalid metadata*");
    }

    private static PrivateArtifactStoreHttpClient Client(
        HttpMessageHandler handler,
        string tokenFile)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PrivateArtifactStoreOptions.BaseUrlKey] = "http://artifact-owner.test/",
                [PrivateArtifactStoreOptions.BearerTokenFileKey] = tokenFile
            })
            .Build();
        return new PrivateArtifactStoreHttpClient(
            new HttpClient(handler),
            configuration,
            new PrivateArtifactStoreOptions());
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var keys)
                    ? keys.Single()
                    : null,
                request.Content?.Headers.ContentType?.ToString(),
                body));
            return response(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? Authorization,
        string? IdempotencyKey,
        string? ContentType,
        string? Body);

    private sealed class TempSecret : IDisposable
    {
        private TempSecret(string path, string value)
        {
            Path = path;
            Value = value;
        }

        public string Path { get; }
        public string Value { get; }

        public static TempSecret Create(string value)
        {
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllText(path, value);
            return new TempSecret(path, value);
        }

        public void Dispose() => File.Delete(Path);
    }
}
