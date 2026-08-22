using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.service.ServiceFeedback;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// End-to-end HTTP regression coverage for the post-delivery rating invariant.
/// Both public upstream-backed submit shapes must use the same canonical/legacy
/// completion rule and must reject locally before feedback-service is called.
/// </summary>
public sealed class JeebRatingCompletionInvariantTests
{
    private const string ClientId = "11111111-1111-1111-1111-111111111111";
    private const string JeeberId = "22222222-2222-2222-2222-222222222222";
    private const string OutsiderId = "33333333-3333-3333-3333-333333333333";

    [Theory]
    [InlineData(false, CanonicalDeliveryStatus.Done)]
    [InlineData(false, RequestStatus.Delivered)]
    [InlineData(false, RequestStatus.Rated)]
    [InlineData(true, CanonicalDeliveryStatus.Done)]
    [InlineData(true, RequestStatus.Delivered)]
    [InlineData(true, RequestStatus.Rated)]
    public async Task Submit_CompletedCanonicalOrLegacyStatus_ForwardsRating(
        bool deliveriesScopedRoute,
        string deliveryStatus)
    {
        var feedback = new RecordingFeedbackClient();
        using var factory = NewFactory(feedback);
        var deliveryId = await SeedDeliveryAsync(factory, deliveryStatus);
        var client = MintBearerClient(factory, ClientId);

        var response = await SubmitAsync(client, deliveriesScopedRoute, deliveryId, score: 5);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        feedback.Submissions.Should().ContainSingle();
        feedback.Submissions[0].CorrelationId.Should().Be($"jeeb:delivery:{deliveryId}");
        feedback.Submissions[0].RaterId.Should().Be(Guid.Parse(ClientId));
        feedback.Submissions[0].RateeId.Should().Be(Guid.Parse(JeeberId));
    }

    [Theory]
    [InlineData(false, RequestStatus.Pending)]
    [InlineData(false, CanonicalDeliveryStatus.Ordered)]
    [InlineData(false, CanonicalDeliveryStatus.InTransit)]
    [InlineData(false, CanonicalDeliveryStatus.AtDoor)]
    [InlineData(false, CanonicalDeliveryStatus.Cancelled)]
    [InlineData(true, RequestStatus.Pending)]
    [InlineData(true, CanonicalDeliveryStatus.Ordered)]
    [InlineData(true, CanonicalDeliveryStatus.InTransit)]
    [InlineData(true, CanonicalDeliveryStatus.AtDoor)]
    [InlineData(true, CanonicalDeliveryStatus.Cancelled)]
    public async Task Submit_NonCompletedStatus_Returns409_WithoutCallingFeedback(
        bool deliveriesScopedRoute,
        string deliveryStatus)
    {
        var feedback = new RecordingFeedbackClient();
        using var factory = NewFactory(feedback);
        var deliveryId = await SeedDeliveryAsync(factory, deliveryStatus);
        var client = MintBearerClient(factory, ClientId);

        var response = await SubmitAsync(client, deliveriesScopedRoute, deliveryId, score: 5);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status409Conflict);
        problem.Type.Should().Be("https://jeeb.dev/errors/delivery-not-complete");
        feedback.Submissions.Should().BeEmpty(
            "an in-progress or failed delivery must never reach feedback-service");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_InvalidScore_OnNonCompletedDelivery_Preserves400ValidationPrecedence(
        bool deliveriesScopedRoute)
    {
        var feedback = new RecordingFeedbackClient();
        using var factory = NewFactory(feedback);
        var deliveryId = await SeedDeliveryAsync(factory, RequestStatus.Pending);
        var client = MintBearerClient(factory, ClientId);

        var response = await SubmitAsync(client, deliveriesScopedRoute, deliveryId, score: 0);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        feedback.Submissions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_Outsider_OnNonCompletedDelivery_Preserves403AuthorizationPrecedence(
        bool deliveriesScopedRoute)
    {
        var feedback = new RecordingFeedbackClient();
        using var factory = NewFactory(feedback);
        var deliveryId = await SeedDeliveryAsync(factory, RequestStatus.Pending);
        var client = MintBearerClient(factory, OutsiderId);

        var response = await SubmitAsync(client, deliveriesScopedRoute, deliveryId, score: 5);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        feedback.Submissions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_RepeatedCompletedRequest_PreservesStableUpstreamIdempotencyIdentity(
        bool deliveriesScopedRoute)
    {
        var feedback = new RecordingFeedbackClient();
        using var factory = NewFactory(feedback);
        var deliveryId = await SeedDeliveryAsync(factory, CanonicalDeliveryStatus.Done);
        var client = MintBearerClient(factory, ClientId);

        var first = await SubmitAsync(client, deliveriesScopedRoute, deliveryId, score: 4);
        var retry = await SubmitAsync(client, deliveriesScopedRoute, deliveryId, score: 4);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        feedback.Submissions.Should().HaveCount(2);
        feedback.Submissions.Select(x => (x.CorrelationId, x.RaterId))
            .Distinct()
            .Should().ContainSingle(
                "feedback-service idempotency remains keyed by the same delivery correlation and rater");
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        bool deliveriesScopedRoute,
        string deliveryId,
        int score)
        => deliveriesScopedRoute
            ? client.PostAsJsonAsync($"/v1/ratings/jeeb/deliveries/{deliveryId}", new { stars = score })
            : client.PostAsJsonAsync("/v1/ratings/jeeb/submit", new { deliveryId, score });

    private static async Task<string> SeedDeliveryAsync(
        WebApplicationFactory<Program> factory,
        string deliveryStatus)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(
            new CreateRequestInput { ClientId = ClientId, Description = "rating-completion-invariant" },
            CancellationToken.None);

        (await store.SetJeeberIdAsync(created.Id, JeeberId, CancellationToken.None))
            .Should().BeTrue();
        (await store.SetStatusAsync(created.Id, deliveryStatus, CancellationToken.None))
            .Should().BeTrue();
        return created.Id;
    }

    private static WebApplicationFactory<Program> NewFactory(RecordingFeedbackClient feedback)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureFlags:UseUpstream:Ratings"] = "true",
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceFeedbackClient>();
                services.AddScoped<ServiceFeedbackClient>(_ => feedback);
            });
        });

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory, string subject)
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = configuration["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = configuration["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = configuration["Jwt:SigningKey"]
            ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", subject), new Claim("roles", "client") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class RecordingFeedbackClient : ServiceFeedbackClient
    {
        public RecordingFeedbackClient()
            : base("http://feedback.test/", new HttpClient())
        {
        }

        public List<SubmitBlindRatingRequest> Submissions { get; } = new();

        public override Task<SubmitBlindRatingResponse> RatingsSubmitAsync(
            SubmitBlindRatingRequest body,
            CancellationToken cancellationToken)
        {
            Submissions.Add(body);
            return Task.FromResult(new SubmitBlindRatingResponse
            {
                Id = Guid.NewGuid(),
                State = new BlindRevealStateResponse
                {
                    CorrelationId = body.CorrelationId,
                    Revealed = false,
                    SubmittedCount = 1,
                    Self = new BlindRatingPartyState
                    {
                        Submitted = true,
                        Score = body.Score,
                        Comment = body.Comment,
                        Tags = body.Tags,
                    },
                    Counterparty = new BlindRatingPartyState { Submitted = false },
                },
            });
        }
    }
}
