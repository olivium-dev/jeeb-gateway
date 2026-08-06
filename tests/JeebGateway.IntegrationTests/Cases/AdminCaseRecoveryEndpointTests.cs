using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class AdminCaseRecoveryEndpointTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-05T00:10:00+00:00");

    [Fact]
    public async Task Push_Recovery_Is_Admin_Only()
    {
        using var factory = Factory(new FakePushRecovery());

        (await factory.CreateClient().GetAsync(
            "/admin/v1/case-recovery/push-dispatches/stale"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(factory, "client-1", "customer").GetAsync(
            "/admin/v1/case-recovery/push-dispatches/stale"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(factory, "admin-1", "admin").GetAsync(
            "/admin/v1/case-recovery/push-dispatches/stale"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Push_Resolve_Proxies_Operator_Audit_Contract()
    {
        var push = new FakePushRecovery();
        using var factory = Factory(push);
        var response = await Client(factory, "admin-1", "admin").PostAsJsonAsync(
            "/admin/v1/case-recovery/push-dispatches/550e8400-e29b-41d4-a716-446655440000/resolve",
            new
            {
                outcome = "succeeded", note = "FCM console confirms delivery",
                response_message = "confirmed", observed_version = 7,
                observed_updated_at = ObservedAt,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        push.Resolution.Should().NotBeNull();
        push.Resolution!.Value.Key.Should().Be("550e8400-e29b-41d4-a716-446655440000");
        push.Resolution.Value.Request.Outcome.Should().Be("succeeded");
        push.Resolution.Value.Request.Note.Should().Be("FCM console confirms delivery");
        push.Resolution.Value.Request.ObservedVersion.Should().Be(7);
        push.Resolution.Value.Request.ObservedUpdatedAt.Should().Be(ObservedAt);
    }

    [Fact]
    public async Task Push_Resolve_Rejects_Either_Missing_Observation_Token_Without_Upstream_Call()
    {
        var push = new FakePushRecovery();
        using var factory = Factory(push);
        var admin = Client(factory, "admin-1", "admin");
        var missingVersion = await admin.PostAsJsonAsync(
            "/admin/v1/case-recovery/push-dispatches/key-1/resolve",
            new
            {
                outcome = "failed", note = "Provider audit found no delivery",
                observed_updated_at = ObservedAt,
            });
        var missingTimestamp = await admin.PostAsJsonAsync(
            "/admin/v1/case-recovery/push-dispatches/key-1/resolve",
            new
            {
                outcome = "failed", note = "Provider audit found no delivery",
                observed_version = 7,
            });

        missingVersion.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        missingTimestamp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Resolution.Should().BeNull();
    }

    [Fact]
    public async Task Push_Resolve_Surfaces_Observed_Record_Cas_Conflict()
    {
        var push = new FakePushRecovery
        {
            ResolveError = new PushDispatchRecoveryApiException(
                409, "Dispatch changed after it was observed"),
        };
        using var factory = Factory(push);
        var response = await Client(factory, "admin-1", "admin").PostAsJsonAsync(
            "/admin/v1/case-recovery/push-dispatches/key-1/resolve",
            new
            {
                outcome = "failed", note = "Provider audit found no delivery",
                observed_version = 7, observed_updated_at = ObservedAt,
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Dispatch changed after it was observed");
    }

    [Fact]
    public async Task Push_Resolve_Rejects_Invalid_Outcome_Without_Upstream_Call()
    {
        var push = new FakePushRecovery();
        using var factory = Factory(push);
        var response = await Client(factory, "admin-1", "admin").PostAsJsonAsync(
            "/admin/v1/case-recovery/push-dispatches/key-1/resolve",
            new { outcome = "retry", note = "do it again" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        push.Resolution.Should().BeNull();
    }

    [Fact]
    public async Task Callback_Dead_Letters_Relay_Cursor_And_Require_Admin_Idempotent_Requeue()
    {
        var state = new FakeRecoveryState();
        using var factory = Factory(new FakePushRecovery(), state);
        var customer = Client(factory, "client-1", "customer");
        (await customer.GetAsync("/admin/v1/case-recovery/callback-dead-letters"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = Client(factory, "admin-1", "admin");
        var list = await admin.GetAsync(
            "/admin/v1/case-recovery/callback-dead-letters?limit=25&cursor=opaque%2Bcursor");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        state.ListCall.Should().Be((25, "opaque+cursor"));

        var eventId = Guid.Parse("30000000-0000-4000-8000-000000000001");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/admin/v1/case-recovery/callback-dead-letters/{eventId:D}/requeue")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", "requeue-1");
        var requeue = await admin.SendAsync(request);

        requeue.StatusCode.Should().Be(HttpStatusCode.OK);
        state.RequeueCall.Should().Be((eventId, "requeue-1", "admin-1"));
    }

    [Fact]
    public async Task Private_Push_Client_Uses_Exact_Stale_Get_And_Resolve_Contracts_Without_Service_Auth()
    {
        var handler = new RecordingHandler();
        var client = new PushDispatchRecoveryClient(new FixedHttpClientFactory(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:10040/") }));

        await client.ListStaleAsync(600, 50, default);
        await client.GetAsync("key/with slash", 900, default);
        await client.ResolveAsync("key-1", new PushDispatchResolutionV1
        {
            Outcome = "failed", Note = "FCM rejected it", ResponseMessage = "operator resolved",
            ObservedVersion = 7, ObservedUpdatedAt = ObservedAt,
        }, default);

        handler.Requests[0].PathAndQuery.Should().Be(
            "/api/v1/sent-payload/idempotency/stale?older_than_seconds=600&limit=50");
        handler.Requests[1].PathAndQuery.Should().Be(
            "/api/v1/sent-payload/idempotency/key%2Fwith%20slash?stale_after_seconds=900");
        handler.Requests[2].PathAndQuery.Should().Be(
            "/api/v1/sent-payload/idempotency/key-1/resolve");
        handler.Requests.Should().OnlyContain(request =>
            !request.Headers.ContainsKey("Authorization") && !request.Headers.ContainsKey("X-Api-Key"));
        using var body = JsonDocument.Parse(handler.Requests[2].Body!);
        body.RootElement.GetProperty("outcome").GetString().Should().Be("failed");
        body.RootElement.GetProperty("response_message").GetString().Should().Be("operator resolved");
        body.RootElement.GetProperty("observed_version").GetInt32().Should().Be(7);
        body.RootElement.GetProperty("observed_updated_at").GetDateTimeOffset().Should().Be(ObservedAt);
    }

    private static WebApplicationFactory<Program> Factory(
        IPushDispatchRecoveryClient push, IGenericCaseStateClient? state = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPushDispatchRecoveryClient>();
            services.AddSingleton(push);
            if (state is not null)
            {
                services.RemoveAll<IGenericCaseStateClient>();
                services.AddSingleton(state);
            }
        }));

    private static HttpClient Client(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    private static PushDispatchStatusV1 Status(string key = "key-1") => new()
    {
        IdempotencyKey = key, TargetUserId = "client-1", State = "claimed", Version = 7, Stale = true,
        CreatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:10:00Z"),
    };

    private sealed class FakePushRecovery : IPushDispatchRecoveryClient
    {
        public (string Key, PushDispatchResolutionV1 Request)? Resolution { get; private set; }
        public PushDispatchRecoveryApiException? ResolveError { get; init; }
        public Task<PushDispatchListV1> ListStaleAsync(int older, int limit, CancellationToken ct) =>
            Task.FromResult(new PushDispatchListV1 { Items = new[] { Status() }, Count = 1 });
        public Task<PushDispatchStatusV1> GetAsync(string key, int stale, CancellationToken ct) =>
            Task.FromResult(Status(key));
        public Task<PushDispatchStatusV1> ResolveAsync(
            string key, PushDispatchResolutionV1 request, CancellationToken ct)
        {
            Resolution = (key, request);
            if (ResolveError is not null) throw ResolveError;
            return Task.FromResult(Status(key));
        }
    }

    private sealed class FakeRecoveryState : IGenericCaseStateClient
    {
        public (int Limit, string? Cursor)? ListCall { get; private set; }
        public (Guid EventId, string Key, string Actor)? RequeueCall { get; private set; }

        public Task<GenericCaseDeadLetterPageV1> GetCaseDeadLettersAsync(
            int limit, string? cursor, CancellationToken ct)
        {
            ListCall = (limit, cursor);
            return Task.FromResult(new GenericCaseDeadLetterPageV1
            {
                Items = Array.Empty<GenericCaseDeadLetterV1>(), NextCursor = "next-state-cursor",
            });
        }

        public Task<GenericCaseDeadLetterRequeueV1> RequeueCaseDeadLetterAsync(
            Guid eventId, string key, string actorRef, CancellationToken ct)
        {
            RequeueCall = (eventId, key, actorRef);
            return Task.FromResult(new GenericCaseDeadLetterRequeueV1
            {
                EventId = eventId, RequeuedAt = DateTimeOffset.Parse("2026-08-06T12:00:00Z"),
            });
        }

        public Task<GenericCaseV1> CreateCaseAsync(CreateGenericCaseRequestV1 body, string key,
            string actorRef, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenericCaseV1> GetCaseAsync(Guid caseId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GenericCasePageV1> ListCasesAsync(GenericCaseQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GenericCaseV1> PatchCaseAsync(Guid caseId, PatchGenericCaseRequestV1 body,
            string key, string actorRef, string actorRole, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GenericCaseMessageCreatedV1> AddCaseMessageAsync(Guid caseId,
            CreateGenericCaseMessageRequestV1 body, string key, string actorRef, string actorRole,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GenericCaseMessageV1>> GetCaseMessagesAsync(
            Guid caseId, bool includeInternal, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GenericCaseAttachmentV1>> GetCaseAttachmentsAsync(
            Guid caseId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GenericCaseAuditEventV1>> GetCaseAuditAsync(
            Guid caseId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.PathAndQuery,
                request.Headers.ToDictionary(item => item.Key, item => item.Value.ToArray()),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            object response = request.RequestUri.AbsolutePath.EndsWith("/stale", StringComparison.Ordinal)
                ? new PushDispatchListV1 { Items = new[] { Status() }, Count = 1 }
                : Status();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(
                    response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(
        string PathAndQuery, IReadOnlyDictionary<string, string[]> Headers, string? Body);
}
