using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Ratings;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Users.DataExport;

/// <summary>
/// The gateway-side consumer of feedback-service's internal per-user rating export.
/// Before it, the packager resolved <see cref="InMemoryDataExportRatingsProvider"/>,
/// which nothing seeds outside tests: every production GDPR export shipped
/// <c>"ratings": []</c> while feedback-service held the rows.
///
/// <para>Pins the four properties that make the consumer correct: the opaque-id
/// derivation matches the write path, the credential comes from the mounted file, the
/// partition filter keeps a shared service's other products out, and an upstream
/// failure is FATAL rather than a silently empty section.</para>
/// </summary>
public class FeedbackServiceDataExportRatingsProviderTests
{
    private const string JeebUserId = "b4c26077-0000-4000-8000-000000000001";
    private const string Token = "feedback-export-token-0123456789abcdef";

    [Fact]
    public async Task Maps_upstream_rows_and_authenticates_with_the_mounted_token()
    {
        HttpRequestMessage? seen = null;
        var deliveryId = "DEL-77";
        var counterparty = Guid.NewGuid();
        var ratingId = Guid.NewGuid();

        var provider = NewProvider(
            request =>
            {
                seen = request;
                return Json(new
                {
                    hasMore = false,
                    ratings = new[]
                    {
                        new
                        {
                            id = ratingId,
                            correlationId = "jeeb:delivery:" + deliveryId,
                            direction = "given",
                            counterpartyId = counterparty,
                            score = 5,
                            comment = "fast",
                            tags = new[] { "jeeb", "role:sami", "punctuality" },
                            createdAt = "2026-08-01T09:30:00Z"
                        }
                    }
                });
            },
            out var tokenFile);

        try
        {
            var snapshots = await provider.GetForUserAsync(JeebUserId, CancellationToken.None);

            // Same opaque id the write path submits under, or the export finds nothing.
            var expectedOpaqueId = FeedbackServiceRatingStore.StableGuid(JeebUserId);
            seen!.RequestUri!.AbsolutePath.Should().Be("/internal/ratings/export");
            seen.RequestUri.Query.Should().Contain($"userId={expectedOpaqueId:D}");
            seen.Headers.GetValues("X-Feedback-Service-Token").Should().ContainSingle().Which.Should().Be(Token);

            snapshots.Should().ContainSingle();
            var only = snapshots[0];
            only.RatingId.Should().Be(ratingId.ToString("D"));
            only.RequestId.Should().Be(deliveryId, "the jeeb:delivery: linkage is re-read gateway-side");
            only.Direction.Should().Be("given");
            only.CounterpartyId.Should().Be(counterparty.ToString("D"));
            only.Stars.Should().Be(5);
            only.Comment.Should().Be("fast");
            only.CreatedAt.Should().Be(new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero));
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    [Fact]
    public async Task Excludes_rows_belonging_to_another_product_on_the_shared_service()
    {
        var provider = NewProvider(
            _ => Json(new
            {
                hasMore = false,
                ratings = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        correlationId = "jeeb:delivery:DEL-1",
                        direction = "received",
                        counterpartyId = Guid.NewGuid(),
                        score = 4,
                        comment = (string?)null,
                        tags = new[] { "jeeb" },
                        createdAt = "2026-08-02T09:00:00Z"
                    },
                    new
                    {
                        id = Guid.NewGuid(),
                        correlationId = "saawt:booking:42",
                        direction = "given",
                        counterpartyId = Guid.NewGuid(),
                        score = 1,
                        comment = (string?)null,
                        tags = new[] { "saawt" },
                        createdAt = "2026-08-03T09:00:00Z"
                    }
                }
            }),
            out var tokenFile);

        try
        {
            var snapshots = await provider.GetForUserAsync(JeebUserId, CancellationToken.None);

            snapshots.Should().ContainSingle();
            snapshots[0].RequestId.Should().Be("DEL-1");
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    [Fact]
    public async Task Tolerates_a_null_rows_array_and_an_offset_timestamp()
    {
        var provider = NewProvider(
            _ => Json(new
            {
                hasMore = false,
                ratings = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        correlationId = "jeeb:delivery:DEL-9",
                        direction = "given",
                        counterpartyId = Guid.NewGuid(),
                        score = 3,
                        comment = (string?)null,
                        tags = new[] { "jeeb" },
                        // Non-Z offset: a bare DateTime would have read this as local time.
                        createdAt = "2026-08-04T11:00:00+02:00"
                    }
                }
            }),
            out var tokenFile);

        try
        {
            var snapshots = await provider.GetForUserAsync(JeebUserId, CancellationToken.None);
            snapshots.Should().ContainSingle();
            snapshots[0].CreatedAt.Should().Be(new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero));
        }
        finally
        {
            File.Delete(tokenFile);
        }

        var nullRows = NewProvider(_ => Json(new { hasMore = false, ratings = (object?)null }), out var second);
        try
        {
            (await nullRows.GetForUserAsync(JeebUserId, CancellationToken.None)).Should().BeEmpty();
        }
        finally
        {
            File.Delete(second);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Upstream_failure_is_fatal_never_a_silently_empty_ratings_section(HttpStatusCode status)
    {
        var provider = NewProvider(_ => new HttpResponseMessage(status), out var tokenFile);

        try
        {
            var act = async () => await provider.GetForUserAsync(JeebUserId, CancellationToken.None);

            (await act.Should().ThrowAsync<DataExportRatingsUnavailableException>())
                .WithMessage($"*{(int)status}*");
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    [Fact]
    public async Task Transport_failure_is_fatal_too()
    {
        var provider = NewProvider(
            _ => throw new HttpRequestException("connection refused"),
            out var tokenFile);

        try
        {
            var act = async () => await provider.GetForUserAsync(JeebUserId, CancellationToken.None);

            await act.Should().ThrowAsync<DataExportRatingsUnavailableException>();
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    [Fact]
    public async Task Token_file_is_trimmed_so_a_normal_secret_file_authorizes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"feedback-export-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, Token + "\n");

        try
        {
            var read = await FeedbackExportCredentialHandler.ReadTokenAsync(path, CancellationToken.None);
            read.Should().Be(Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_or_short_token_file_fails_loudly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"feedback-export-absent-{Guid.NewGuid():N}");
        var act = async () => await FeedbackExportCredentialHandler.ReadTokenAsync(missing, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var shortFile = Path.Combine(Path.GetTempPath(), $"feedback-export-short-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(shortFile, "too-short");
        try
        {
            var shortAct = async () =>
                await FeedbackExportCredentialHandler.ReadTokenAsync(shortFile, CancellationToken.None);
            await shortAct.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            File.Delete(shortFile);
        }

        var relative = async () =>
            await FeedbackExportCredentialHandler.ReadTokenAsync("relative/path/token", CancellationToken.None);
        await relative.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Unconfigured_options_keep_the_previous_in_memory_binding()
    {
        new FeedbackRatingExportOptions().IsConfigured.Should().BeFalse(
            "no token file means the wiring must stay exactly as it was for CI/dev");
        new FeedbackRatingExportOptions { ServiceTokenFile = "/secrets/t", Enabled = false }
            .IsConfigured.Should().BeFalse("the kill switch must win over a mounted secret");
        new FeedbackRatingExportOptions { ServiceTokenFile = "/secrets/t" }
            .IsConfigured.Should().BeTrue();
    }

    /// <summary>
    /// The defect was a DI binding, not a missing class: assert the registration the
    /// composition root calls flips once the secret is mounted, and otherwise stays on
    /// the in-memory double the existing DataExportEndpointTests seed.
    /// </summary>
    [Fact]
    public void Registration_binds_the_feedback_consumer_only_when_the_secret_is_mounted()
    {
        Resolve(new Dictionary<string, string?>())
            .Should().BeOfType<InMemoryDataExportRatingsProvider>();

        Resolve(new Dictionary<string, string?>
        {
            ["FeedbackServiceApi:BaseUrl"] = "http://feedback.test",
            ["Users:DataExport:FeedbackRatings:ServiceTokenFile"] = "/secrets/feedback-export-token"
        }).Should().BeOfType<FeedbackServiceDataExportRatingsProvider>();

        Resolve(new Dictionary<string, string?>
        {
            ["FeedbackServiceApi:BaseUrl"] = "http://feedback.test",
            ["Users:DataExport:FeedbackRatings:ServiceTokenFile"] = "/secrets/feedback-export-token",
            ["Users:DataExport:FeedbackRatings:Enabled"] = "false"
        }).Should().BeOfType<InMemoryDataExportRatingsProvider>();
    }

    [Fact]
    public void Registration_refuses_a_mounted_secret_without_a_feedback_base_url()
    {
        var act = () => Resolve(new Dictionary<string, string?>
        {
            ["Users:DataExport:FeedbackRatings:ServiceTokenFile"] = "/secrets/feedback-export-token"
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*FeedbackServiceApi:BaseUrl is unset*");
    }

    private static IDataExportRatingsProvider Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataExportRatingsProvider(configuration);
        return services.BuildServiceProvider().GetRequiredService<IDataExportRatingsProvider>();
    }

    private static FeedbackServiceDataExportRatingsProvider NewProvider(
        Func<HttpRequestMessage, HttpResponseMessage> upstream,
        out string tokenFile)
    {
        tokenFile = Path.Combine(Path.GetTempPath(), $"feedback-export-{Guid.NewGuid():N}");
        File.WriteAllText(tokenFile, Token + "\n");

        var options = new FeedbackRatingExportOptions { ServiceTokenFile = tokenFile };
        var credential = new FeedbackExportCredentialHandler(options)
        {
            InnerHandler = new StubHandler(upstream)
        };
        var http = new HttpClient(credential) { BaseAddress = new Uri("http://feedback.test/") };

        return new FeedbackServiceDataExportRatingsProvider(
            new SingleClientFactory(http),
            options,
            NullLogger<FeedbackServiceDataExportRatingsProvider>.Instance);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
