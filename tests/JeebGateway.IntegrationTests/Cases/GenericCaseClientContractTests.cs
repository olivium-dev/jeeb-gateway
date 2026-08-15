using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class GenericCaseClientContractTests
{
    private static readonly Guid CaseId = Guid.Parse("489660be-7844-42bc-a48f-f5c707b85b25");

    [Fact]
    public async Task Create_Matches_Canonical_State_Contract_Exactly()
    {
        var handler = new RecordingHandler(_ => JsonResponse(Case(1)));
        var client = Client(handler);
        await client.CreateCaseAsync(new CreateGenericCaseRequestV1
        {
            Kind = "dispute", Category = "damaged",
            Subject = new GenericCaseSubjectV1 { Type = "delivery", Ref = "delivery-1" },
            RequesterRef = "client-1", Status = "pending", Priority = "normal",
            ParticipantRefs = new[] { "client-1", "courier-1" },
            Attachments = new[] { new GenericCaseAttachmentCreateV1 { CdnRef = "dispute_evidence/object-1" } },
        }, "create-key", "client-1", "client", default);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be("/cases");
        request.Headers["Idempotency-Key"].Should().Be("create-key");
        request.Headers["X-Actor-Ref"].Should().Be("client-1");
        request.Headers["X-Actor-Role"].Should().Be("client");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.EnumerateObject().Select(item => item.Name).Should().BeEquivalentTo(
            "kind", "category", "subject", "requesterRef", "status", "priority",
            "participantRefs", "assigneeRef", "dueAt", "attachments");
        body.RootElement.GetProperty("subject").GetProperty("ref").GetString().Should().Be("delivery-1");
        body.RootElement.GetProperty("participantRefs").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("client-1", "courier-1");
        body.RootElement.TryGetProperty("callback", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("participants", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("evidence", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("refund", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Patch_Uses_Body_Cas_And_Actor_Headers_Not_IfMatch()
    {
        var handler = new RecordingHandler(_ => JsonResponse(Case(4)));
        var client = Client(handler);
        await client.PatchCaseAsync(CaseId, new PatchGenericCaseRequestV1
        {
            ExpectedVersion = 3, Status = "fixed", Priority = "high",
        }, "patch-key", "admin-1", "admin", default);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Patch);
        request.Uri.AbsolutePath.Should().Be($"/cases/{CaseId:D}");
        request.Headers.Should().NotContainKey("If-Match");
        request.Headers["Idempotency-Key"].Should().Be("patch-key");
        request.Headers["X-Actor-Ref"].Should().Be("admin-1");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(3);
        body.RootElement.GetProperty("status").GetString().Should().Be("fixed");
    }

    [Fact]
    public async Task Status_Message_Uses_Atomic_State_Command_With_Cas_And_Actor_Headers()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new GenericCaseStatusMessageV1
        {
            Case = Case(5),
            Message = Message(5, "Package replacement arranged."),
        }));
        var client = Client(handler);

        await client.ApplyCaseStatusMessageAsync(CaseId, new ApplyGenericCaseStatusMessageRequestV1
        {
            ExpectedVersion = 4,
            Status = GenericCaseStatuses.Fixed,
            Body = "Package replacement arranged.",
        }, "status-message-key", "admin-1", "admin", default);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsolutePath.Should().Be($"/cases/{CaseId:D}/status-message");
        request.Headers["Idempotency-Key"].Should().Be("status-message-key");
        request.Headers["X-Actor-Ref"].Should().Be("admin-1");
        request.Headers["X-Actor-Role"].Should().Be("admin");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("expectedVersion").GetInt32().Should().Be(4);
        body.RootElement.GetProperty("status").GetString().Should().Be(GenericCaseStatuses.Fixed);
        body.RootElement.GetProperty("body").GetString().Should().Be("Package replacement arranged.");
    }

    [Fact]
    public async Task Messages_And_Queue_Use_Canonical_Routes_And_Filter_Names()
    {
        var call = 0;
        var handler = new RecordingHandler(_ => ++call == 1
            ? JsonResponse(new GenericCaseMessageCreatedV1
            {
                Message = new GenericCaseMessageV1
                {
                    MessageId = Guid.NewGuid(), CaseId = CaseId, MessageType = "internal_note",
                    Body = "metadata", Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
                }, CaseVersion = 2,
            })
            : JsonResponse(new GenericCasePageV1 { Items = new[] { Case(2) }, NextCursor = "state-cursor" }));
        var client = Client(handler);

        await client.AddCaseMessageAsync(CaseId, new CreateGenericCaseMessageRequestV1
        {
            ExpectedVersion = 1, MessageType = "internal_note", Body = "metadata",
        }, "message-key", "admin-1", "admin", default);
        await client.ListCasesAsync(new GenericCaseQueryV1
        {
            Kind = "support", Status = "open", Priority = "urgent", AssigneeRef = "agent-1",
            Assigned = true,
            RequesterRef = "client-1", ParticipantRef = "courier-1",
            SubjectType = "delivery", SubjectRef = "delivery-1",
            DueBefore = DateTimeOffset.Parse("2026-08-06T00:00:00Z"), Active = true, Limit = 20,
            Sort = GenericCaseSorts.Sla,
            Cursor = "opaque+/=cursor",
        }, default);

        handler.Requests[0].Uri.AbsolutePath.Should().Be($"/cases/{CaseId:D}/messages");
        handler.Requests[1].Uri.PathAndQuery.Should().Be(
            "/cases?kind=support&status=open&priority=urgent&assigneeRef=agent-1"
            + "&assigned=true&requesterRef=client-1&participantRef=courier-1"
            + "&subjectType=delivery&subjectRef=delivery-1"
            + "&dueBefore=2026-08-06T00%3A00%3A00.0000000%2B00%3A00"
            + "&active=true&sort=sla&limit=20&cursor=opaque%2B%2F%3Dcursor");
    }

    [Fact]
    public async Task Recent_List_Query_Omits_Blank_Participant_And_Keeps_Cursor_In_Sort_Scope()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            new GenericCasePageV1 { NextCursor = "next-recent-cursor" }));

        await Client(handler).ListCasesAsync(new GenericCaseQueryV1
        {
            ParticipantRef = "   ", Sort = GenericCaseSorts.Recent,
            Limit = 25, Cursor = "recent+/=cursor",
        }, default);

        handler.Requests.Single().Uri.PathAndQuery.Should().Be(
            "/cases?sort=recent&limit=25&cursor=recent%2B%2F%3Dcursor");
    }

    [Fact]
    public async Task Message_Page_Relays_State_Cursor_And_Requested_Bound()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new GenericCaseMessagePageV1
        {
            Items = Array.Empty<GenericCaseMessageV1>(), NextCursor = "earlier-state-cursor",
        }));
        var page = await Client(handler).GetCaseMessagesPageAsync(
            CaseId, includeInternal: false, limit: 25, cursor: "opaque+/=message", default);

        handler.Requests.Single().Uri.PathAndQuery.Should().Be(
            $"/cases/{CaseId:D}/messages?includeInternal=false&order=newest&limit=25&cursor=opaque%2B%2F%3Dmessage");
        page.NextCursor.Should().Be("earlier-state-cursor");
    }

    [Fact]
    public async Task Message_Page_Preserves_State_CaseVersion_Order_And_Dto_Fields()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new GenericCaseMessagePageV1
        {
            Items = new[]
            {
                Message(9, "newer-clock"),
                Message(10, "older-clock"),
            },
        }));

        var page = await Client(handler).GetCaseMessagesPageAsync(
            CaseId, includeInternal: false, limit: 20, cursor: null, default);

        page.Items.Select(item => item.CaseVersion).Should().Equal(9, 10);
        page.Items.Select(item => item.Body).Should().Equal("newer-clock", "older-clock");
    }

    [Fact]
    public async Task Oldest_Message_Page_Uses_A_Distinct_State_Order_Scope()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new GenericCaseMessagePageV1()));

        await Client(handler).GetCaseMessagesPageAsync(
            CaseId, includeInternal: true, GenericCaseMessageOrders.Oldest,
            limit: 1, cursor: null, default);

        handler.Requests.Single().Uri.PathAndQuery.Should().Be(
            $"/cases/{CaseId:D}/messages?includeInternal=true&order=oldest&limit=1");
    }

    [Fact]
    public async Task Dead_Letter_Recovery_Uses_Canonical_Admin_Headers_And_Routes()
    {
        var eventId = Guid.Parse("30000000-0000-4000-8000-000000000001");
        var call = 0;
        var handler = new RecordingHandler(_ => ++call == 1
            ? JsonResponse(new GenericCaseDeadLetterPageV1
            {
                Items = Array.Empty<GenericCaseDeadLetterV1>(), NextCursor = "dead-letter-cursor",
            })
            : JsonResponse(new GenericCaseDeadLetterRequeueV1
            {
                EventId = eventId, RequeuedAt = DateTimeOffset.Parse("2026-08-06T12:00:00Z"),
            }));
        var client = Client(handler);

        await client.GetCaseDeadLettersAsync(20, "opaque+/=dead", default);
        await client.RequeueCaseDeadLetterAsync(eventId, "requeue-1", "admin-1", default);

        handler.Requests[0].Uri.PathAndQuery.Should().Be(
            "/case-outbox/dead-letters?limit=20&cursor=opaque%2B%2F%3Ddead");
        handler.Requests[0].Headers["X-Actor-Role"].Should().Be("admin");
        handler.Requests[1].Uri.AbsolutePath.Should().Be(
            $"/case-outbox/dead-letters/{eventId:D}/requeue");
        handler.Requests[1].Headers["Idempotency-Key"].Should().Be("requeue-1");
        handler.Requests[1].Headers["X-Actor-Ref"].Should().Be("admin-1");
        handler.Requests[1].Headers["X-Actor-Role"].Should().Be("admin");
        handler.Requests.Should().OnlyContain(request =>
            !request.Headers.ContainsKey("Authorization") && !request.Headers.ContainsKey("X-Api-Key"));
    }

    private static JeebStateServiceClient Client(HttpMessageHandler handler) => new(
        "https://state.example/", new HttpClient(handler) { BaseAddress = new Uri("https://state.example/") });

    private static GenericCaseV1 Case(int version) => new()
    {
        CaseId = CaseId, Kind = "dispute", Category = "damaged",
        Subject = new GenericCaseSubjectV1 { Type = "delivery", Ref = "delivery-1" },
        RequesterRef = "client-1", Status = "pending", Priority = "normal", Version = version,
        CreatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
    };

    private static GenericCaseMessageV1 Message(int caseVersion, string body) => new()
    {
        MessageId = Guid.NewGuid(), CaseId = CaseId, MessageType = "message", Body = body,
        Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
        CaseVersion = caseVersion,
        CreatedAt = caseVersion == 9
            ? DateTimeOffset.Parse("2026-08-06T12:00:00Z")
            : DateTimeOffset.Parse("2026-08-06T11:00:00Z"),
    };

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.AsEnumerable();
            if (request.Content is not null)
                headers = headers.Concat(request.Content.Headers);

            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!,
                headers
                    .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }
    private sealed record CapturedRequest(HttpMethod Method, Uri Uri,
        IReadOnlyDictionary<string, string> Headers, string? Body);
}
