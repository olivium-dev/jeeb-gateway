using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Ratings;
using JeebGateway.service.ServiceFeedback;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using UserManagementClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// D-W2 — the jeeber reputation aggregate never populated: the profile read and the offer-card
/// enrichment both queried the legacy per-tag comment surface
/// (<c>GET /Review/comment?Tag=&lt;jeeberId&gt;</c>), which NOTHING writes for jeeber tags, so a
/// jeeber with 8 revealed 5★ ratings still rendered "No reviews yet" and
/// <c>rating:null, ratingCount:0</c>. Both readers now use the list-by-RATEE aggregate the blind
/// rating WRITE path actually populates (<c>GET /ratings/ratee/{rateeId}/reviews</c>), keyed by the
/// same <see cref="FeedbackServiceRatingStore.StableGuid"/> derivation the write path stamps.
///
/// <para>These tests pin the UPSTREAM CALL (path + id) as well as the projected values, because the
/// defect was invisible from the response shape alone — the wrong surface answered 200 with zeros.</para>
/// </summary>
public class JeebReviewsRateeAggregateTests
{
    private const string CallerGuid = "44444444-4444-4444-4444-444444444444";
    private const string JeeberGuid = "b4c26077-0985-40a1-b799-ec001bc9ad10";
    private const string RaterGuid = "77777777-7777-7777-7777-777777777777";
    private const int RevealedReviewCount = 8;
    private const double UpstreamAverage = 4.875;

    [Fact]
    public async Task ListReviews_Reads_The_Ratee_Aggregate_And_Projects_The_Real_Count_And_Average()
    {
        var recorder = new PathRecordingHandler(RateeReviewsResponder);

        using var factory = NewFactoryWithFeedbackStub(recorder);
        var client = MintBearerClient(factory, CallerGuid);

        var resp = await client.GetAsync(
            $"/v1/ratings/jeeb/reviews?jeeberId={JeeberGuid}&page=1&pageSize=5");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<ReviewsPage>();

        page!.TotalCount.Should().Be(RevealedReviewCount, "the ratee aggregate has 8 revealed rows");
        page.ReviewCount.Should().Be(RevealedReviewCount);
        page.AverageScore.Should().BeApproximately(4.88, 0.001);
        page.ColdStart.Should().BeFalse("8 >= the D59 cold-start threshold");
        page.Items.Should().HaveCount(5);
        page.Items[0].Score.Should().Be(5);

        // The id crossing the boundary is the write path's own derivation — a real GUID
        // round-trips unchanged, so profile reads and rating writes cannot disagree.
        recorder.Paths.Should().Contain(p =>
            p.Contains($"ratings/ratee/{FeedbackServiceRatingStore.StableGuid(JeeberGuid)}/reviews",
                StringComparison.OrdinalIgnoreCase));
        recorder.Paths.Should().NotContain(p => p.Contains("/Review/comment", StringComparison.OrdinalIgnoreCase),
            "the legacy per-tag comment surface is never written for jeeber tags");
    }

    [Fact]
    public async Task ListReviews_Upstream_Failure_Still_Fails_Closed_With_502()
    {
        // The read swap must not weaken provenance: an upstream outage is still 502, never an
        // empty page presented as truth.
        var stub = new PathRecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            });

        using var factory = NewFactoryWithFeedbackStub(stub);
        var client = MintBearerClient(factory, CallerGuid);

        var resp = await client.GetAsync($"/v1/ratings/jeeb/reviews?jeeberId={JeeberGuid}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task ListReviews_Resolves_Revealed_Rater_Via_UserManagement_And_Emits_First_Name_Only()
    {
        var feedback = new PathRecordingHandler(RateeReviewsResponder);
        var profiles = new PathRecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(RaterGuid, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        userId = RaterGuid,
                        username = "Nour Khaled",
                    }),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var factory = NewFactoryWithFeedbackStub(feedback, profiles);
        var client = MintBearerClient(factory, CallerGuid);

        var resp = await client.GetAsync(
            $"/v1/ratings/jeeb/reviews?jeeberId={JeeberGuid}&page=1&pageSize=5");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<ReviewsPage>();
        page!.Items[0].ReviewerFirstName.Should().Be("Nour");
        page.Items[0].ReviewerFirstName.Should().NotContain("Khaled",
            "D58 permits first-name attribution only");
        profiles.Paths.Should().ContainSingle(path =>
            path.EndsWith($"/api/User/profile/{RaterGuid}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListReviews_Profile_Failure_Degrades_To_Blank_Without_Failing_Review_Read()
    {
        var feedback = new PathRecordingHandler(RateeReviewsResponder);
        var profiles = new PathRecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("profile unavailable", Encoding.UTF8, "text/plain"),
            });

        using var factory = NewFactoryWithFeedbackStub(feedback, profiles);
        var client = MintBearerClient(factory, CallerGuid);

        var resp = await client.GetAsync(
            $"/v1/ratings/jeeb/reviews?jeeberId={JeeberGuid}&page=1&pageSize=5");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "profile enrichment is optional presentation data, not review provenance");
        var page = await resp.Content.ReadFromJsonAsync<ReviewsPage>();
        page!.Items.Should().OnlyContain(row => row.ReviewerFirstName == string.Empty);
        page.Items.Should().HaveCount(5, "valid revealed reviews must survive an identity outage");
    }

    [Fact]
    public async Task Offer_Enrichment_Reads_The_Ratee_Aggregate_For_The_Offer_Card_Rating()
    {
        // The offer card showed rating:null / ratingCount:0 for the same reason. Same swap,
        // same id derivation.
        var recorder = new PathRecordingHandler(RateeReviewsResponder);
        var enricher = Enricher(recorder);

        var enriched = await enricher.EnrichAsync(new[] { Offer(JeeberGuid) }, CancellationToken.None);

        var dto = enriched.Should().ContainSingle().Subject;
        dto.Rating.Should().BeApproximately(4.88, 0.001);
        dto.RatingCount.Should().Be(RevealedReviewCount);

        recorder.Paths.Should().Contain(p =>
            p.Contains($"ratings/ratee/{FeedbackServiceRatingStore.StableGuid(JeeberGuid)}/reviews",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Offer_Enrichment_Still_Degrades_To_Null_Rating_When_The_Aggregate_Read_Fails()
    {
        // Failure mode is unchanged by the swap: enrichment degrades, the offer still lists.
        var stub = new PathRecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var enricher = Enricher(stub);

        var enriched = await enricher.EnrichAsync(new[] { Offer(JeeberGuid) }, CancellationToken.None);

        var dto = enriched.Should().ContainSingle().Subject;
        dto.Rating.Should().BeNull();
        dto.RatingCount.Should().Be(0);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serves the live-shaped ratee aggregate for the ratee route and 404s everything else, so a
    /// read pointed at the wrong upstream surface fails the test loudly instead of silently
    /// returning zeros (which is exactly how D-W2 hid).
    /// </summary>
    private static HttpResponseMessage RateeReviewsResponder(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (!path.Contains("/ratings/ratee/", StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith("/reviews", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var reviews = Enumerable.Range(0, 5).Select(i => new
        {
            id = Guid.NewGuid(),
            raterId = i == 0 ? Guid.Parse(RaterGuid) : Guid.NewGuid(),
            score = 5,
            comment = i == 0 ? "On time and careful" : null,
            tags = Array.Empty<string>(),
            createdAt = DateTimeOffset.UtcNow.AddDays(-i),
            revealedAt = DateTimeOffset.UtcNow.AddDays(-i),
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                rateeId = FeedbackServiceRatingStore.StableGuid(JeeberGuid),
                reviews,
                totalReviewCount = RevealedReviewCount,
                averageRating = UpstreamAverage,
            }),
        };
    }

    private static OfferJeeberEnricher Enricher(HttpMessageHandler handler)
    {
        var feedback = new ServiceFeedbackClient(
            "http://feedback.test/", new HttpClient(handler) { BaseAddress = new Uri("http://feedback.test/") });
        // The profile leg is irrelevant here and degrades to null names on its own.
        var profiles = new UserManagementClient(
            "http://um.test/",
            new HttpClient(new PathRecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            {
                BaseAddress = new Uri("http://um.test/"),
            });

        return new OfferJeeberEnricher(
            profiles,
            feedback,
            Options.Create(new GatewayPublicOptions()),
            NullLogger<OfferJeeberEnricher>.Instance);
    }

    private static PendingOffer Offer(string jeeberId) => new()
    {
        Id = "offer-1",
        RequestId = "req-1",
        JeeberId = jeeberId,
        Status = PendingOfferStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        Fee = 5m,
        EtaMinutes = 20,
    };

    private static WebApplicationFactory<Program> NewFactoryWithFeedbackStub(
        HttpMessageHandler stub,
        HttpMessageHandler? profileStub = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceFeedbackClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://feedback.test/") };
                    return new ServiceFeedbackClient("http://feedback.test/", http);
                });

                services.RemoveAll<UserManagementClient>();
                services.AddScoped(_ =>
                {
                    var handler = profileStub ?? new PathRecordingHandler(
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound));
                    var http = new HttpClient(handler) { BaseAddress = new Uri("http://um.test/") };
                    return new UserManagementClient("http://um.test/", http);
                });
            });
        });

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory, string sub)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", sub), new Claim("roles", "client") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class PathRecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public PathRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;

        public ConcurrentBag<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed record ReviewsPage(
        string JeeberId,
        List<ReviewRow> Items,
        int Page,
        int PageSize,
        int TotalCount,
        int TotalPages,
        bool ColdStart,
        int ReviewCount,
        double? AverageScore);

    private sealed record ReviewRow(string Id, string? ReviewerFirstName, int Score, string? Body, string CreatedAt, bool Reportable);
}
