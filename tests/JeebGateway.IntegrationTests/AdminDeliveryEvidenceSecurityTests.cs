using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using FluentAssertions;
using JeebGateway.Conversations.Client;
using JeebGateway.Controllers;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class AdminDeliveryEvidenceSecurityTests
{
    private const string RawReference = "proof_of_delivery/delivery-42.jpg";
    private const string OpaqueOwnerReference = "private-bucket/opaque-owner-object";

    [Fact]
    public async Task ListRewritesOwnerReferenceAndEvidenceStreamsOnlyAfterOwnerVerification()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var cdnHandler = new CdnHandler();
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
            ["cdn-proxy"] = new(cdnHandler) { BaseAddress = new Uri("http://cdn.test/") },
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminEvidence:TokenKey"] = "unit-test-evidence-signing-key-32-bytes-minimum",
        }).Build();
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var listResult = await controller.Index(
            null, null, null, null, null, null, null, null, null, CancellationToken.None);
        var content = listResult.Should().BeOfType<ContentResult>().Subject.Content!;

        content.Should().NotContain(RawReference);
        content.Should().NotContain(OpaqueOwnerReference);
        var list = JsonNode.Parse(content)!;
        var browserPath = list["data"]![0]!["evidence_url"]!.GetValue<string>();
        browserPath.Should().StartWith("/gateway/admin/v1/deliveries/delivery-42/evidence/");
        var token = browserPath.Split('/').Last();

        var evidenceResult = await controller.Evidence("delivery-42", token, CancellationToken.None);

        var file = evidenceResult.Should().BeOfType<FileStreamResult>().Subject;
        using var reader = new StreamReader(file.FileStream, Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be("proof-bytes");
        cdnHandler.LastRequest.Should().NotBeNull();
        cdnHandler.LastRequest!.Host.Should().Be("cdn.test");
        cdnHandler.LastRequest.AbsolutePath.Should().StartWith("/api/ImageUpload/fetch/");
        controller.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        controller.Response.Headers.ContentDisposition.ToString().Should().Be("attachment; filename=\"evidence.jpg\"");
    }

    [Fact]
    public async Task EvidenceRejectsAValidTokenFromAnotherDeliveryBeforeCdnDial()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var cdnHandler = new CdnHandler();
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
            ["cdn-proxy"] = new(cdnHandler) { BaseAddress = new Uri("http://cdn.test/") },
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminEvidence:TokenKey"] = "unit-test-evidence-signing-key-32-bytes-minimum",
        }).Build();
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var list = (ContentResult)await controller.Index(
            null, null, null, null, null, null, null, null, null, CancellationToken.None);
        var token = JsonNode.Parse(list.Content!)!["data"]![0]!["evidence_url"]!
            .GetValue<string>().Split('/').Last();

        var result = await controller.Evidence("delivery-other", token, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        cdnHandler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task EvidenceRejectsScriptableMimeEvenForAnOwnerVerifiedReference()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var cdnHandler = new CdnHandler { ContentType = "text/html" };
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
            ["cdn-proxy"] = new(cdnHandler) { BaseAddress = new Uri("http://cdn.test/") },
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminEvidence:TokenKey"] = "unit-test-evidence-signing-key-32-bytes-minimum",
        }).Build();
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var list = (ContentResult)await controller.Index(
            null, null, null, null, null, null, null, null, null, CancellationToken.None);
        var token = JsonNode.Parse(list.Content!)!["data"]![0]!["evidence_url"]!
            .GetValue<string>().Split('/').Last();

        var result = await controller.Evidence("delivery-42", token, CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should()
            .Be(StatusCodes.Status415UnsupportedMediaType);
        controller.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
    }

    [Fact]
    public async Task EvidenceRejectsOversizedContentBeforeStreaming()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var cdnHandler = new CdnHandler { DeclaredContentLength = 26L * 1024 * 1024 };
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
            ["cdn-proxy"] = new(cdnHandler) { BaseAddress = new Uri("http://cdn.test/") },
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminEvidence:TokenKey"] = "unit-test-evidence-signing-key-32-bytes-minimum",
        }).Build();
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var list = (ContentResult)await controller.Index(
            null, null, null, null, null, null, null, null, null, CancellationToken.None);
        var token = JsonNode.Parse(list.Content!)!["data"]![0]!["evidence_url"]!
            .GetValue<string>().Split('/').Last();

        var result = await controller.Evidence("delivery-42", token, CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should()
            .Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task EvidenceStreamRejectsContentThatExceedsItsDeclaredLength()
    {
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(new DeliveryOwnerHandler()) { BaseAddress = new Uri("http://delivery.test/") },
            ["cdn-proxy"] = new(new CdnHandler { DeclaredContentLength = 4 }) { BaseAddress = new Uri("http://cdn.test/") },
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminEvidence:TokenKey"] = "unit-test-evidence-signing-key-32-bytes-minimum",
        }).Build();
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var list = (ContentResult)await controller.Index(
            null, null, null, null, null, null, null, null, null, CancellationToken.None);
        var token = JsonNode.Parse(list.Content!)!["data"]![0]!["evidence_url"]!
            .GetValue<string>().Split('/').Last();

        var result = await controller.Evidence("delivery-42", token, CancellationToken.None);
        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        var consume = async () => await file.FileStream.CopyToAsync(Stream.Null);

        await consume.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*exceeded its declared Content-Length*");
    }

    [Fact]
    public async Task DetailComposesBoundedRouteAndChatUsingTheActiveDeliveryClient()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
            ["admin-settlements"] = new(),
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminEvidence:TokenKey"] = "unit-test-evidence-signing-key-32-bytes-minimum",
        }).Build();
        var geo = new GeoHistory();
        var chat = new ConversationEvidence();
        var controller = new AdminDeliveriesController(
            factory, null!, null!, geo, chat, configuration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("sub", "admin-1"),
                        new Claim("roles", "admin"),
                    }, "test")),
                },
            },
        };

        var result = await controller.Detail("delivery-42", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ok.Value))!;
        payload["data"]!["evidence"]!["route"]!["retentionDays"]!.GetValue<int>().Should().Be(30);
        payload["data"]!["evidence"]!["route"]!["truncated"]!.GetValue<bool>().Should().BeTrue();
        payload["data"]!["evidence"]!["chat"]!["truncated"]!.GetValue<bool>().Should().BeTrue();
        payload.ToJsonString().Should().NotContain("raw-private-attachment");
        payload.ToJsonString().Should().NotContain(OpaqueOwnerReference,
            "arbitrary owner fields must never be exposed by the aggregate");
        payload["data"]!["fieldAvailability"]!["destination"]!["status"]!.GetValue<string>()
            .Should().Be("unavailable");
        payload["data"]!["sourceHealth"]!["chat"]!["status"]!.GetValue<string>().Should().Be("available");
        chat.Viewer.Should().Be("client-42");
        chat.Limit.Should().Be(200);
        geo.TrackId.Should().Be("delivery-42");
        geo.Limit.Should().Be(500);
    }

    [Fact]
    public async Task DeliveryReadDoesNotGrantCaseOrSettlementEvidenceCapabilities()
    {
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(new DeliveryOwnerHandler())
                { BaseAddress = new Uri("http://delivery.test/") },
        });
        var controller = new AdminDeliveriesController(
            factory, null!, null!, new GeoHistory(), new ConversationEvidence(),
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("sub", "operations-1"),
                        new Claim("roles", "operations"),
                    }, "test")),
                },
            },
        };

        var result = await controller.Detail("delivery-42", CancellationToken.None);

        var payload = JsonNode.Parse(JsonSerializer.Serialize(
            result.Should().BeOfType<OkObjectResult>().Subject.Value))!;
        payload["data"]!["evidence"]!["relatedCases"].Should().BeNull();
        payload["data"]!["evidence"]!["codSettlements"].Should().BeNull();
        payload["data"]!["sourceHealth"]!["cases"]!["reason"]!.GetValue<string>()
            .Should().Be("admin_cases_read_required");
        payload["data"]!["sourceHealth"]!["settlements"]!["reason"]!.GetValue<string>()
            .Should().Be("admin_settlements_read_required");
    }

    [Fact]
    public async Task FreshMfaTransitionForwardsExactOwnerContractWithoutCredentials()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
        });
        var now = DateTimeOffset.UtcNow;
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, new ConfigurationBuilder().Build(),
            new FixedTimeProvider(now))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("sub", "ops-42"),
                        new Claim("amr", "pwd mfa"),
                        new Claim("auth_time", now.ToUnixTimeSeconds().ToString()),
                    }, "test")),
                },
            },
        };

        var result = await controller.Transition(
            "delivery/42",
            new JeebGateway.Admin.AdminDeliveryTransitionRequest(
                "FailedNeedsEscalation", "InTransit", "  unsafe address  "),
            "delivery-operation-42",
            CancellationToken.None);

        result.Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(StatusCodes.Status200OK);
        deliveryHandler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        deliveryHandler.LastRequest.RequestUri!.PathAndQuery.Should()
            .Be("/api/v1/admin/deliveries/delivery%2F42/transition");
        deliveryHandler.LastRequest.Headers.GetValues("X-Admin-Id").Should().Equal("ops-42");
        deliveryHandler.LastRequest.Headers.GetValues("Idempotency-Key").Should().Equal("delivery-operation-42");
        deliveryHandler.LastRequest.Headers.Authorization.Should().BeNull();
        deliveryHandler.LastRequest.Headers.Contains("X-Api-Key").Should().BeFalse();
        deliveryHandler.LastBody.Should().Contain("\"expected_status\":\"InTransit\"")
            .And.Contain("\"reason\":\"unsafe address\"");
        controller.Response.Headers["Idempotency-Replayed"].ToString().Should().Be("false");
    }

    [Fact]
    public async Task TransitionWithoutFreshMfaFailsBeforeOwnerDial()
    {
        var deliveryHandler = new DeliveryOwnerHandler();
        var factory = new NamedClientFactory(new Dictionary<string, HttpClient>
        {
            ["admin-deliveries-owner"] = new(deliveryHandler) { BaseAddress = new Uri("http://delivery.test/") },
        });
        var controller = new AdminDeliveriesController(
            factory, null!, null!, null!, null!, new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("sub", "ops-42") }, "test")),
                },
            },
        };

        var result = await controller.Transition(
            "delivery-42",
            new JeebGateway.Admin.AdminDeliveryTransitionRequest(
                "Cancelled", "Ordered", "customer requested cancellation"),
            "delivery-operation-42",
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        deliveryHandler.LastRequest.Should().BeNull();
    }

    private sealed class NamedClientFactory(IReadOnlyDictionary<string, HttpClient> clients)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clients[name];
    }

    private sealed class DeliveryOwnerHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                LastRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            var deliveryId = path.Contains("delivery-other", StringComparison.Ordinal)
                ? "delivery-other"
                : "delivery-42";
            var json = request.Method == HttpMethod.Post
                ? $$$"""{"delivery_id":"{{{deliveryId}}}","previous_status":"InTransit","status":"FailedNeedsEscalation","transition_id":"transition-42","transitioned_at":"2026-08-07T10:00:00Z"}"""
                : path.EndsWith("/timeline", StringComparison.Ordinal)
                ? $$$"""{"delivery_id":"{{{deliveryId}}}","timeline":[{"evidence_url":"{{{RawReference}}}","opaque_object_reference":"{{{OpaqueOwnerReference}}}"}],"internal_metadata":{"object":"{{{OpaqueOwnerReference}}}"}}"""
                : path.EndsWith("/deliveries", StringComparison.Ordinal)
                    ? $$$"""{"data":[{"delivery_id":"delivery-42","evidence_url":"{{{RawReference}}}","opaque_object_reference":"{{{OpaqueOwnerReference}}}"}]}"""
                    : $$$"""{"delivery_id":"{{{deliveryId}}}","party_ids":{"client_id":"client-42","courier_id":null},"evidence_url":"{{{RawReference}}}","opaque_object_reference":"{{{OpaqueOwnerReference}}}","internal_metadata":{"object":"{{{OpaqueOwnerReference}}}"}}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (request.Method == HttpMethod.Post)
                response.Headers.TryAddWithoutValidation("Idempotency-Replayed", "false");
            return response;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CdnHandler : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }
        public string ContentType { get; init; } = "image/jpeg";
        public long? DeclaredContentLength { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("proof-bytes", Encoding.UTF8, ContentType),
            };
            if (DeclaredContentLength is not null)
                response.Content.Headers.ContentLength = DeclaredContentLength;
            return Task.FromResult(response);
        }
    }

    private sealed class GeoHistory : IGeoHistoryClient
    {
        public string? TrackId { get; private set; }
        public int Limit { get; private set; }

        public Task RecordTrackPointAsync(string trackId, string actorId, double lat, double lng,
            double? accuracyM, DateTimeOffset recordedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GpsTrackHistoryPage> GetTrackHistoryPageAsync(string trackId, string? cursor,
            int limit = 500, CancellationToken cancellationToken = default)
        {
            TrackId = trackId;
            Limit = limit;
            return Task.FromResult(new GpsTrackHistoryPage
            {
                Available = true,
                TrackId = trackId,
                Pings = new[]
                {
                    new GpsTrackHistoryPoint
                    {
                        Lat = 33.89,
                        Lng = 35.50,
                        RecordedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                },
                RetentionDays = 30,
                RetainedFrom = DateTimeOffset.Parse("2026-07-08T00:00:00Z"),
                HasMore = true,
                NextCursor = "geo-next",
            });
        }
    }

    private sealed class ConversationEvidence : IJeebConversationClient
    {
        public string? Viewer { get; private set; }
        public int Limit { get; private set; }

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct) => Task.FromResult(new JeebConversationResponse
        {
            ConversationId = "conversation-42",
            CorrelationKey = correlationKey,
            Phase = "accepted",
            Participants = new List<JeebConversationParticipant>
            {
                new() { UserId = "client-42", RoleInConvo = "client" },
                new() { UserId = "removed-client", RoleInConvo = "client", RemovedAt = DateTimeOffset.UtcNow },
            },
        });

        public Task<JeebConversationExportPage> ExportMessagesForViewerAsync(
            string conversationId, string viewerUserId, DateTimeOffset? asOf, string? cursor,
            int limit, CancellationToken ct)
        {
            Viewer = viewerUserId;
            Limit = limit;
            return Task.FromResult(new JeebConversationExportPage
            {
                ConversationId = conversationId,
                ViewerId = viewerUserId,
                AsOf = DateTimeOffset.Parse("2026-08-07T10:00:00Z"),
                Messages = new List<JeebMessageResponse>
                {
                    new()
                    {
                        MessageId = "message-42", Kind = "structured", Subtype = "delivery.proof",
                        AuthorId = "client-42", Body = "Delivered", CreatedAt = DateTime.Parse("2026-08-07T09:59:00Z"),
                        Payload = JsonDocument.Parse("{\"attachment\":\"raw-private-attachment\"}")
                            .RootElement.Clone(),
                    },
                },
                HasMore = true,
                NextCursor = "chat-next",
                Limit = limit,
            });
        }

        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeebConversationResponse> GetConversationByIdAsync(
            string conversationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<JeebConversationMembership> GetMembershipAsync(
            string conversationId, string viewerUserId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<JeebConversationParticipant> AddParticipantAsync(
            string conversationId, AddJeebParticipantRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<JeebConversationResponse> AdvancePhaseAsync(
            string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<JeebConversationSettleResponse> SettleAsync(
            string conversationId, SettleJeebConversationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
