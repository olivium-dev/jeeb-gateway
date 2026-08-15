using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Cms;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.Cms;

public sealed class BundlerCmsSurfaceStoreTests
{
    [Fact]
    public async Task List_Uses_Cms_Scoped_Bearer_And_Maps_Owner_Heads()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, """
            {"documents":[{
              "documentId":"d-1","namespace":"jeeb.cms","key":"ofl-cms-orders-mfe",
              "currentDraftVersion":2,"currentPublication":1,"currentPublishedVersion":1,
              "updatedAt":"2026-08-10T00:00:00Z"
            }],"count":1,"hasMore":true,"nextAfter":"ofl-cms-orders-mfe"}
            """),
            Json(HttpStatusCode.OK, """
            {"documents":[{
              "documentId":"d-2","namespace":"jeeb.cms","key":"ofl-cms-users-mfe",
              "currentDraftVersion":1,"currentPublication":1,"currentPublishedVersion":1,
              "updatedAt":"2026-08-10T00:00:00Z"
            }],"count":1,"hasMore":false}
            """),
            Json(HttpStatusCode.OK, """
            {"document":{"documentId":"d-1","namespace":"jeeb.cms","key":"ofl-cms-orders-mfe",
              "currentDraftVersion":2,"currentPublication":1,"currentPublishedVersion":1,
              "updatedAt":"2026-08-10T00:00:00Z"},
             "draft":{"version":2,"content":{"title":"Orders Console","config":{"draft":true}}}}
            """),
            Json(HttpStatusCode.OK, """
            {"document":{"documentId":"d-2","namespace":"jeeb.cms","key":"ofl-cms-users-mfe",
              "currentDraftVersion":1,"currentPublication":1,"currentPublishedVersion":1,
              "updatedAt":"2026-08-10T00:00:00Z"},
             "draft":{"version":1,"content":{"title":"Users Console","config":{"live":true}}}}
            """),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var secret = TemporarySecret.Create();
        var store = Store(handler, secret.Path);

        var surfaces = await store.ListSurfacesAsync(CancellationToken.None);

        surfaces.Should().HaveCount(2);
        surfaces[0].SurfaceId.Should().Be("ofl-cms-orders-mfe");
        surfaces[0].LatestPublishedVersion.Should().Be(1);
        surfaces[0].Draft.Should().NotBeNull();
        surfaces[0].Title.Should().Be("Orders Console");
        surfaces[1].Title.Should().Be("Users Console");
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/namespaces/jeeb.cms/documents?limit=200&archived=false",
            "/api/v1/namespaces/jeeb.cms/documents?limit=200&archived=false&after=ofl-cms-orders-mfe",
            "/api/v1/namespaces/jeeb.cms/documents/ofl-cms-orders-mfe",
            "/api/v1/namespaces/jeeb.cms/documents/ofl-cms-users-mfe");
        handler.Requests.Should().OnlyContain(request =>
            request.Authorization == $"Bearer {TemporarySecret.Value}");
    }

    [Fact]
    public async Task Get_Hydrates_Draft_And_Immutable_Publication_History()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, """
                {"document":{"documentId":"d-1","namespace":"cms","key":"surface-a",
                 "currentDraftVersion":2,"currentPublication":1,"currentPublishedVersion":1,
                 "updatedAt":"2026-08-10T00:00:00Z"},
                 "draft":{"version":2,"content":{"title":"Surface A","config":{"banner":"draft"}}},
                 "published":{"version":1,"content":{"title":"Surface A","config":{"banner":"live"}}},
                 "publication":{"publication":1,"version":1,"publishedBy":"admin-1",
                  "publishedAt":"2026-08-09T00:00:00Z"}}
                """),
            Json(HttpStatusCode.OK, """
                {"versions":[
                  {"version":1,"content":{"title":"Surface A","config":{"banner":"live"}}},
                  {"version":2,"content":{"title":"Surface A","config":{"banner":"draft"}}}
                ],"count":2}
                """),
            Json(HttpStatusCode.OK, """
                {"publications":[{"publication":1,"version":1,"publishedBy":"admin-1",
                  "publishedAt":"2026-08-09T00:00:00Z"}],"count":1}
                """),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var secret = TemporarySecret.Create();
        var store = Store(handler, secret.Path);

        var surface = await store.GetSurfaceAsync("surface-a", CancellationToken.None);

        surface.Should().NotBeNull();
        surface!.Title.Should().Be("Surface A");
        surface.Draft!.Data["banner"].ToString().Should().Be("draft");
        surface.Versions.Should().ContainSingle();
        surface.Versions[0].Config.Data["banner"].ToString().Should().Be("live");
        surface.Versions[0].PublishedByUserId.Should().Be("admin-1");
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/namespaces/jeeb.cms/documents/surface-a",
            "/api/v1/namespaces/jeeb.cms/documents/surface-a/versions?after=0&limit=200",
            "/api/v1/namespaces/jeeb.cms/documents/surface-a/publications?after=0&limit=200");
    }

    [Fact]
    public async Task UpsertDraft_Preserves_Owner_Title_In_Jeeb_Envelope()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, """
                {"document":{"documentId":"d-1","namespace":"jeeb.cms","key":"surface-a",
                 "currentDraftVersion":1,"currentPublication":1,"currentPublishedVersion":1,
                 "updatedAt":"2026-08-10T00:00:00Z"},
                 "draft":{"version":1,"content":{"title":"Editable title","config":{"old":true}}}}
                """),
            Json(HttpStatusCode.Created, "{}"),
            Json(HttpStatusCode.OK, """
                {"document":{"documentId":"d-1","namespace":"jeeb.cms","key":"surface-a",
                 "currentDraftVersion":2,"currentPublication":1,"currentPublishedVersion":1,
                 "updatedAt":"2026-08-10T00:01:00Z"},
                 "draft":{"version":2,"content":{"title":"Editable title","config":{"banner":"new"}}}}
                """),
            Json(HttpStatusCode.OK, """
                {"versions":[
                  {"version":1,"content":{"title":"Editable title","config":{"old":true}}},
                  {"version":2,"content":{"title":"Editable title","config":{"banner":"new"}}}
                ]}
                """),
            Json(HttpStatusCode.OK, """
                {"publications":[{"publication":1,"version":1,"publishedBy":"admin-1",
                  "publishedAt":"2026-08-10T00:00:00Z"}]}
                """),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var secret = TemporarySecret.Create();
        var store = Store(handler, secret.Path);

        var surface = await store.UpsertDraftAsync(
            "surface-a",
            new CmsConfig
            {
                Data = new Dictionary<string, object?> { ["banner"] = "new" },
            },
            CancellationToken.None);

        surface!.Title.Should().Be("Editable title");
        using var body = System.Text.Json.JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("expectedDraftVersion").GetInt64().Should().Be(1);
        body.RootElement.GetProperty("content").GetProperty("title").GetString()
            .Should().Be("Editable title");
        body.RootElement.GetProperty("content").GetProperty("config")
            .GetProperty("banner").GetString().Should().Be("new");
    }

    [Fact]
    public async Task Missing_Service_Credential_File_Fails_Closed_Before_Http()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"documents":[],"count":0,"hasMore":false}"""));
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"missing-bundler-{Guid.NewGuid():N}");
        var store = Store(handler, path);

        var act = () => store.ListSurfacesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*BUNDLER_CMS_BEARER_TOKEN_FILE*");
        handler.Requests.Should().BeEmpty();
    }

    private static BundlerCmsSurfaceStore Store(RecordingHandler handler, string tokenFile)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://bundler.test/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [BundlerCmsSurfaceStore.NamespaceConfigurationKey] = "jeeb.cms",
                [BundlerCmsSurfaceStore.BearerTokenFileConfigurationKey] = tokenFile,
            }).Build();
        return new BundlerCmsSurfaceStore(new FixedHttpClientFactory(http), config);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RequestRecord(string Path, string? Authorization, string? Body);

    private sealed class TemporarySecret : IDisposable
    {
        public const string Value = "cms-test-service-token-0123456789abcdef";

        private TemporarySecret(string path) => Path = path;

        public string Path { get; }

        public static TemporarySecret Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"bundler-token-{Guid.NewGuid():N}");
            File.WriteAllText(
                path, Value + Environment.NewLine, new UTF8Encoding(false));
            return new TemporarySecret(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
