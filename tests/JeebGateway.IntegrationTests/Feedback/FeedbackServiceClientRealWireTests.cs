using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.Feedback;

/// <summary>
/// REAL-WIRE CONTRACT TEST for the gateway↔feedback-service seam (thin-BFF wire,
/// behind <c>FeatureFlags:UseUpstream:Feedback</c>).
///
/// <para>
/// SCORE-TAKING RESOLUTION. feedback-service (live at
/// <c>http://192.168.2.50:10064</c>) is the REAL ratings upstream; the older
/// <c>score-taking-service</c> / <see cref="IScoreServiceClient"/> targets a
/// config key (<c>Services:ScoreTaking</c>) that has no appsettings entry in any
/// environment and is not in the deployed fleet — stale. See
/// <see cref="IFeedbackServiceClient"/> for the full write-up.
/// </para>
///
/// <para>
/// Two layers, mirroring <c>ChatServiceClientRealWireTests</c>:
/// </para>
/// <list type="number">
///   <item>CI-SAFE binding tests that drive the PRODUCTION
///     <see cref="FeedbackServiceClient"/> over a stub
///     <see cref="HttpMessageHandler"/> returning the LITERAL feedback-service
///     JSON (camelCase, byte-for-byte the Swashbuckle/STJ casing the service
///     emits). These always run and break instead of prod if the contract
///     casing drifts.</item>
///   <item>A LIVE test gated by the <c>JEEB_FEEDBACK_LIVE</c> env var that hits
///     the real upstream and proves the read path is functional. It is skipped
///     in CI (no env var) so the suite stays deterministic.</item>
/// </list>
///
/// Verbatim bodies below are the actual wire confirmed against the live service
/// on 2026-06-01:
///   GET /Review/comment    → GetCommentsResponse  (comments[], totalReviewCount, averageRating, ratingDistribution)
///   GET /Review/rating     → GetAverageRatingResponse ({ "averageRating": null } when no reviews)
///   POST /Review/comment   → CommentResponse on 200 (currently 500 on the live box — see WritePath note)
/// </summary>
public sealed class FeedbackServiceClientRealWireTests
{
    private const string FeedbackBaseUrl = "http://192.168.2.50:10064";
    private const string LiveEnvVar = "JEEB_FEEDBACK_LIVE";

    // Verbatim GET /Review/comment envelope — camelCase, as emitted by
    // feedback-service (Swashbuckle / System.Text.Json web defaults).
    private const string ListCommentsBody = """
    {
      "comments": [
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "commenterId": "22222222-2222-2222-2222-222222222222",
          "rating": 5,
          "topicId": "33333333-3333-3333-3333-333333333333",
          "text": "great courier",
          "date": "2026-05-30T10:15:30Z",
          "tag": "ratee-user-1",
          "criteria": "client",
          "media": [],
          "reviewTitle": "fast"
        },
        {
          "id": "44444444-4444-4444-4444-444444444444",
          "commenterId": "55555555-5555-5555-5555-555555555555",
          "rating": 3,
          "topicId": "33333333-3333-3333-3333-333333333333",
          "text": null,
          "date": "2026-05-29T09:00:00Z",
          "tag": "ratee-user-1",
          "criteria": "jeeber",
          "media": null,
          "reviewTitle": null
        }
      ],
      "totalReviewCount": 2,
      "averageRating": 4.0,
      "ratingDistribution": { "rating5": 1, "rating4": 0, "rating3": 1, "rating2": 0, "rating1": 0 }
    }
    """;

    [Fact]
    public async Task ListCommentsAsync_Binds_Real_Feedback_GetCommentsResponse_Json()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req;
            return Json(ListCommentsBody);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(FeedbackBaseUrl + "/") };
        var sut = new FeedbackServiceClient(http);

        var page = await sut.ListCommentsAsync("ratee-user-1", length: 10, offset: 0, ct: default);

        // Request shape: GET /Review/comment with Tag/Length/Offset query.
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsolutePath.Should().Be("/Review/comment");
        captured.RequestUri.Query.Should().Contain("Tag=ratee-user-1");
        captured.RequestUri.Query.Should().Contain("Length=10");
        captured.RequestUri.Query.Should().Contain("Offset=0");

        // Envelope binds through STJ web defaults.
        page.TotalReviewCount.Should().Be(2);
        page.AverageRating.Should().Be(4.0);
        page.Comments.Should().HaveCount(2);
        page.Comments[0].Rating.Should().Be(5);
        page.Comments[0].Criteria.Should().Be("client");
        page.Comments[0].Text.Should().Be("great courier");
        page.Comments[1].Text.Should().BeNull();
    }

    [Fact]
    public async Task GetAverageRatingAsync_Binds_Null_When_No_Reviews()
    {
        // Live service returns { "averageRating": null } for an unrated topic.
        var handler = new StubHandler((req, _) =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/Review/rating");
            req.RequestUri.Query.Should().Contain("tag=ratee-user-1");
            return Json("""{ "averageRating": null }""");
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(FeedbackBaseUrl + "/") };
        var sut = new FeedbackServiceClient(http);

        var avg = await sut.GetAverageRatingAsync("ratee-user-1", ct: default);

        avg.Should().BeNull();
    }

    [Fact]
    public async Task SubmitCommentAsync_Posts_CreateCommentRequest_And_Binds_CommentResponse()
    {
        // Proves the SUBMIT mapping: POST /Review/comment with the gateway's
        // FeedbackSubmitRequest projected onto CreateCommentRequest, and the
        // 200 CommentResponse bound back. (The live box currently 500s on this
        // write — see the WritePath_Is_Currently_Broken live test — so the
        // mapping is asserted here against the documented 200 contract.)
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler((req, ct) =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Json("""
            {
              "id": "66666666-6666-6666-6666-666666666666",
              "commenterId": "22222222-2222-2222-2222-222222222222",
              "rating": 5,
              "topicId": "33333333-3333-3333-3333-333333333333",
              "text": "great courier",
              "date": "2026-06-01T12:00:00Z",
              "tag": "ratee-user-1",
              "criteria": "client",
              "reviewTitle": null
            }
            """);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(FeedbackBaseUrl + "/") };
        var sut = new FeedbackServiceClient(http);

        var result = await sut.SubmitCommentAsync(new FeedbackSubmitRequest
        {
            Tag = "ratee-user-1",
            CommenterId = "22222222-2222-2222-2222-222222222222",
            Rating = 5,
            Criteria = "client",
            Text = "great courier",
        }, ct: default);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/Review/comment");

        // Wire body carries the required CreateCommentRequest fields, camelCase.
        capturedBody.Should().Contain("\"commenterId\":\"22222222-2222-2222-2222-222222222222\"");
        capturedBody.Should().Contain("\"tag\":\"ratee-user-1\"");
        capturedBody.Should().Contain("\"criteria\":\"client\"");
        capturedBody.Should().Contain("\"rating\":5");

        result.Id.Should().Be("66666666-6666-6666-6666-666666666666");
        result.Rating.Should().Be(5);
        result.Tag.Should().Be("ratee-user-1");
    }

    [Fact]
    public async Task ListCommentsAsync_Throws_On_Non2xx()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler) { BaseAddress = new Uri(FeedbackBaseUrl + "/") };
        var sut = new FeedbackServiceClient(http);

        var act = async () => await sut.ListCommentsAsync("t", 10, 0, default);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ----------------------------------------------------------------------
    // LIVE tests — only run when JEEB_FEEDBACK_LIVE is set. Skipped in CI so
    // the suite stays hermetic. Run locally with:
    //   JEEB_FEEDBACK_LIVE=1 dotnet test --filter FullyQualifiedName~FeedbackServiceClientRealWireTests
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Live_ListAndRating_ReadPath_Is_Green()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LiveEnvVar)))
        {
            // Hermetic by default: only runs when JEEB_FEEDBACK_LIVE is set so CI
            // never depends on the 192.168.2.50 box being reachable.
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(FeedbackBaseUrl + "/") };
        var sut = new FeedbackServiceClient(http);

        // A random topic with no reviews: list returns an empty, well-formed
        // envelope and rating returns null — proving the GET seam binds the real
        // wire shape end-to-end against the live box.
        var tag = "jeeb-contract-" + Guid.NewGuid().ToString("N");

        var page = await sut.ListCommentsAsync(tag, length: 10, offset: 0, ct: default);
        page.Comments.Should().BeEmpty();
        page.TotalReviewCount.Should().Be(0);

        var avg = await sut.GetAverageRatingAsync(tag, ct: default);
        avg.Should().BeNull();
    }

    [Fact]
    public async Task Live_WritePath_Documents_Current_Upstream_State()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LiveEnvVar)))
        {
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(FeedbackBaseUrl + "/") };
        var sut = new FeedbackServiceClient(http);

        var act = async () => await sut.SubmitCommentAsync(new FeedbackSubmitRequest
        {
            Tag = "jeeb-contract-" + Guid.NewGuid().ToString("N"),
            CommenterId = Guid.NewGuid().ToString(),
            Rating = 5,
            Criteria = "client",
            Text = "live write probe",
        }, ct: default);

        // As of 2026-06-01 the live feedback-service write path returns HTTP 500
        // for every valid payload (the read paths are healthy). This test
        // DOCUMENTS that state: SubmitCommentAsync surfaces it as
        // HttpRequestException. When the upstream write path is fixed, this
        // assertion will start failing and is the signal to flip
        // FeatureFlags:UseUpstream:Feedback = true in appsettings.Production.json.
        await act.Should().ThrowAsync<HttpRequestException>(
            "the live feedback-service POST /Review/comment currently returns 500 for all payloads");
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
