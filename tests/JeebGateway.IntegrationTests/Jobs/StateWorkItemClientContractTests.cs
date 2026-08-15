using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.StateService.Work;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class StateWorkItemClientContractTests
{
    [Fact]
    public async Task Create_Uses_Exact_Owner_Path_Header_And_CamelCase_Envelope()
    {
        var responseItem = Item();
        var handler = new RecordingHandler((_, _) => Json(responseItem, HttpStatusCode.Created));
        var client = Client(handler);
        var payload = JsonSerializer.SerializeToElement(new { userId = "user-42" });

        var created = await client.CreateAsync(
            "data-export:stable-key",
            new StateWorkItemCreate(
                "jeeb-gateway",
                "data-export",
                "sha256:subject",
                payload,
                DueAt: null,
                MaxAttempts: 10,
                RetainPayloadUntil: null),
            CancellationToken.None);

        created.WorkItemId.Should().Be(responseItem.WorkItemId);
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.PathAndQuery.Should().Be("/v1/work-items");
        request.IdempotencyKey.Should().Be("data-export:stable-key");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("application").GetString().Should().Be("jeeb-gateway");
        body.RootElement.GetProperty("kind").GetString().Should().Be("data-export");
        body.RootElement.GetProperty("subjectRef").GetString().Should().Be("sha256:subject");
        body.RootElement.GetProperty("payload").GetProperty("userId").GetString()
            .Should().Be("user-42");
        body.RootElement.GetProperty("maxAttempts").GetInt32().Should().Be(10);
        body.RootElement.TryGetProperty("dueAt", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("retainPayloadUntil", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reads_Use_Exact_Scoped_Query_And_Map_404_To_Null()
    {
        var handler = new RecordingHandler((_, index) => index == 0
            ? Json(Item(), HttpStatusCode.OK)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(handler);

        var latest = await client.GetLatestAsync(
            "jeeb gateway",
            "data/export",
            "sha256:subject/with space",
            CancellationToken.None);
        var missing = await client.GetAsync(Guid.NewGuid(), CancellationToken.None);

        latest.Should().NotBeNull();
        missing.Should().BeNull();
        handler.Requests[0].PathAndQuery.Should().Be(
            "/v1/work-items/latest?application=jeeb%20gateway&kind=data%2Fexport&subjectRef=sha256%3Asubject%2Fwith%20space");
        handler.Requests[1].PathAndQuery.Should().MatchRegex(
            "^/v1/work-items/[0-9a-f-]{36}$");
    }

    [Fact]
    public async Task Mutations_Use_Exact_Action_Contracts_And_Map_409_To_Cas_Conflict()
    {
        var item = Item();
        var handler = new RecordingHandler((_, index) => index < 5
            ? Json(item, HttpStatusCode.OK)
            : new HttpResponseMessage(HttpStatusCode.Conflict));
        var client = Client(handler);
        var lease = Guid.NewGuid();
        var completedResult = JsonSerializer.SerializeToElement(new { sizeBytes = 123L });
        var retryAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z");

        await client.RenewLeaseAsync(
            item.WorkItemId,
            new StateWorkLeaseRenew(lease, 7, 120),
            CancellationToken.None);
        await client.CompleteAsync(
            item.WorkItemId,
            new StateWorkComplete(
                lease,
                8,
                completedResult,
                "artifact/ref",
                DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
                "sha256:download"),
            CancellationToken.None);
        await client.DeferAsync(
            item.WorkItemId,
            new StateWorkDefer(lease, 9, retryAt, "expected wait"),
            CancellationToken.None);
        await client.FailAsync(
            item.WorkItemId,
            new StateWorkFail(lease, 10, "retry later", retryAt),
            CancellationToken.None);
        await client.ConsumeAsync(
            item.WorkItemId,
            new StateWorkConsume("jeeb-gateway", "sha256:download", 11),
            CancellationToken.None);

        var conflict = () => client.ConsumeAsync(
            item.WorkItemId,
            new StateWorkConsume("jeeb-gateway", "sha256:download", 11),
            CancellationToken.None);
        await conflict.Should().ThrowAsync<StateWorkConflictException>();

        handler.Requests.Select(request => request.PathAndQuery).Should().Equal(
            $"/v1/work-items/{item.WorkItemId:D}/lease",
            $"/v1/work-items/{item.WorkItemId:D}/complete",
            $"/v1/work-items/{item.WorkItemId:D}/defer",
            $"/v1/work-items/{item.WorkItemId:D}/fail",
            $"/v1/work-items/{item.WorkItemId:D}/consume",
            $"/v1/work-items/{item.WorkItemId:D}/consume");

        using var renew = JsonDocument.Parse(handler.Requests[0].Body!);
        renew.RootElement.GetProperty("leaseToken").GetGuid().Should().Be(lease);
        renew.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(7);
        renew.RootElement.GetProperty("leaseSeconds").GetInt32().Should().Be(120);

        using var complete = JsonDocument.Parse(handler.Requests[1].Body!);
        complete.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(8);
        complete.RootElement.GetProperty("artifactRef").GetString().Should().Be("artifact/ref");
        complete.RootElement.GetProperty("downloadTokenHash").GetString()
            .Should().Be("sha256:download");
        complete.RootElement.GetProperty("result").GetProperty("sizeBytes").GetInt64()
            .Should().Be(123);

        using var defer = JsonDocument.Parse(handler.Requests[2].Body!);
        defer.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(9);
        defer.RootElement.GetProperty("dueAt").GetDateTimeOffset().Should().Be(retryAt);
        defer.RootElement.GetProperty("reason").GetString().Should().Be("expected wait");

        using var fail = JsonDocument.Parse(handler.Requests[3].Body!);
        fail.RootElement.GetProperty("error").GetString().Should().Be("retry later");
        fail.RootElement.GetProperty("retryAt").GetDateTimeOffset().Should().Be(retryAt);

        using var consume = JsonDocument.Parse(handler.Requests[4].Body!);
        consume.RootElement.GetProperty("application").GetString().Should().Be("jeeb-gateway");
        consume.RootElement.GetProperty("downloadTokenHash").GetString()
            .Should().Be("sha256:download");
        consume.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(11);
    }

    private static StateWorkItemClient Client(HttpMessageHandler handler) => new(new HttpClient(handler)
    {
        BaseAddress = new Uri("http://state-owner.test/")
    });

    private static StateWorkItem Item() => new()
    {
        WorkItemId = Guid.NewGuid(),
        Application = "jeeb-gateway",
        Kind = "data-export",
        SubjectRef = "sha256:subject",
        Status = "leased",
        Payload = JsonSerializer.SerializeToElement(new { userId = "user-42" }),
        Result = JsonSerializer.SerializeToElement(new { }),
        DueAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
        Attempts = 1,
        MaxAttempts = 10,
        Version = 7,
        LeaseToken = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z")
    };

    private static HttpResponseMessage Json(StateWorkItem item, HttpStatusCode status) => new(status)
    {
        Content = JsonContent.Create(item)
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
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
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null,
                body));
            return response(request, Requests.Count - 1);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string PathAndQuery,
        string? IdempotencyKey,
        string? Body);
}
