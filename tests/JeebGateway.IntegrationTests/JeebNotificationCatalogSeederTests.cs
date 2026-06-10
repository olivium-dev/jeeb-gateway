using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1486 cutover step (2) — proves <see cref="JeebNotificationCatalogSeeder"/>
/// re-registers the relocated <c>jeeb.*</c> catalog into the running
/// notification-service so the deprecated alias stays alive after the de-leak.
///
/// Without this seeder, the catalog's <c>Render</c>/<c>All</c> are referenced
/// only by tests and the live jeeb.* localization is dark in production. These
/// tests pin that the seeder POSTs every catalog key (all 8, EN+AR title/body)
/// to the generic opaque-key <c>POST /templates/register</c> endpoint and that
/// re-running it is idempotent.
/// </summary>
public class JeebNotificationCatalogSeederTests
{
    [Fact]
    public async Task Seeds_All_Eight_Jeeb_Keys_With_Both_Locales_To_Register_Endpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        using var client = NewClient(handler);

        var count = await JeebNotificationCatalogSeeder.SeedAsync(
            client, NullLogger.Instance, CancellationToken.None);

        count.Should().Be(8);
        handler.Requests.Should().HaveCount(8);

        // Every call hit the generic, opaque-key registration endpoint.
        handler.Requests.Should().OnlyContain(r =>
            r.Method == HttpMethod.Post &&
            r.Uri!.AbsolutePath.EndsWith("/" + JeebNotificationCatalogSeeder.RegisterPath));

        var postedKeys = handler.Bodies
            .Select(b => b.RootElement.GetProperty("key").GetString())
            .ToArray();

        postedKeys.Should().BeEquivalentTo(JeebNotificationCatalogTests.ExpectedKeys);

        // Each registration carries EN + AR title/body lifted verbatim from the
        // gateway-owned catalog (this is the copy that was removed from the
        // shared notification-service).
        foreach (var body in handler.Bodies)
        {
            var key = body.RootElement.GetProperty("key").GetString()!;
            var translations = body.RootElement.GetProperty("translations");

            foreach (var locale in new[] { "en", "ar" })
            {
                translations.TryGetProperty(locale, out var t).Should().BeTrue(
                    "key {0} must register the {1} locale", key, locale);

                var expected = JeebNotificationCatalog.Render(key, locale);
                t.GetProperty("title").GetString().Should().Be(expected.Title);
                t.GetProperty("body").GetString().Should().Be(expected.Body);
            }
        }
    }

    [Fact]
    public async Task ReSeeding_Is_Idempotent_And_Replays_The_Same_Payload()
    {
        // The upstream upserts on key, so re-running the seeder (every
        // deploy/restart) must simply re-post the same keys without error.
        var handler = new RecordingHandler(HttpStatusCode.Created);
        using var client = NewClient(handler);

        var first = await JeebNotificationCatalogSeeder.SeedAsync(
            client, NullLogger.Instance, CancellationToken.None);
        var second = await JeebNotificationCatalogSeeder.SeedAsync(
            client, NullLogger.Instance, CancellationToken.None);

        first.Should().Be(8);
        second.Should().Be(8);
        handler.Requests.Should().HaveCount(16);

        // Both passes post the identical set of keys — no drift across re-runs.
        var firstKeys = handler.Bodies.Take(8)
            .Select(b => b.RootElement.GetProperty("key").GetString());
        var secondKeys = handler.Bodies.Skip(8)
            .Select(b => b.RootElement.GetProperty("key").GetString());
        secondKeys.Should().BeEquivalentTo(firstKeys);
    }

    [Fact]
    public async Task Throws_On_Upstream_Failure_So_The_Retry_Loop_Reattempts()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var client = NewClient(handler);

        var act = async () => await JeebNotificationCatalogSeeder.SeedAsync(
            client, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static HttpClient NewClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://notification.test/") };

    /// <summary>Captures every outbound request + its JSON body for assertions.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) => _status = status;

        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();
        public List<JsonDocument> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));
            if (request.Content is not null)
            {
                var json = await request.Content.ReadAsStringAsync(cancellationToken);
                Bodies.Add(JsonDocument.Parse(json));
            }

            return new HttpResponseMessage(_status);
        }
    }
}
