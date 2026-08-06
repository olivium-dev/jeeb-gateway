using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class CaseMobileContractTests
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-08-05T09:00:00Z");

    [Fact]
    public void Dispute_Detail_Projects_Evidence_Timeline_And_ObjectRef_String_Attachments()
    {
        var caseId = Guid.Parse("489660be-7844-42bc-a48f-f5c707b85b25");
        var evidence = new GenericCaseEvidenceV1
        {
            Source = "gps_pings", Status = "partial", Marker = "truncated_max_points",
            Count = 500, CapturedAt = BaseTime,
        };
        var detail = new GenericCaseDetailV1
        {
            Case = Case(caseId, "dispute", BaseTime),
            Messages = new[]
            {
                Message(caseId, Guid.Parse("10000000-0000-0000-0000-000000000001"), BaseTime, "message"),
                Message(caseId, Guid.Parse("10000000-0000-0000-0000-000000000002"), BaseTime.AddMinutes(1),
                    "internal_note", CaseApiProjection.MetadataBody(new CaseGatewayMetadataV1
                    { Subject = "Damaged parcel", Evidence = new[] { evidence } })),
            },
            Attachments = new[]
            {
                Attachment(caseId, null, "dispute_evidence/photo-1.jpg"),
                Attachment(caseId, null, "dispute_evidence/photo-2.jpg"),
            },
            Audit = new[]
            {
                new GenericCaseAuditEventV1
                {
                    EventId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                    CaseId = caseId, EventType = "case.created",
                    Actor = new GenericCaseActorV1 { Ref = "client-1", Role = "client" },
                    CaseVersion = 1, CreatedAt = BaseTime,
                    Data = JsonSerializer.SerializeToElement(new { }),
                },
                new GenericCaseAuditEventV1
                {
                    EventId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                    CaseId = caseId, EventType = "case.message_added",
                    Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
                    CaseVersion = 2, CreatedAt = BaseTime.AddMinutes(1),
                    Data = JsonSerializer.SerializeToElement(new { messageType = "internal_note" }),
                },
                new GenericCaseAuditEventV1
                {
                    EventId = Guid.Parse("20000000-0000-0000-0000-000000000003"),
                    CaseId = caseId, EventType = "case.message_added",
                    Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
                    CaseVersion = 3, CreatedAt = BaseTime.AddMinutes(2),
                    Data = JsonSerializer.SerializeToElement(new { messageType = "message" }),
                },
            },
        };

        var response = CaseApiProjection.Project(detail, includeInternal: false);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        response.Evidence.Should().ContainSingle(item => item.Source == "gps_pings");
        response.Timeline.Should().HaveCount(2)
            .And.NotContain(item => item.EventId
                == Guid.Parse("20000000-0000-0000-0000-000000000002"));
        CaseApiProjection.Project(detail, includeInternal: true).Timeline.Should().HaveCount(3);
        response.Attachments.Should().Equal(
            "dispute_evidence/photo-1.jpg", "dispute_evidence/photo-2.jpg");
        json.RootElement.GetProperty("attachments").EnumerateArray()
            .Should().OnlyContain(item => item.ValueKind == JsonValueKind.String);
        json.RootElement.GetProperty("evidence")[0].GetProperty("marker").GetString()
            .Should().Be("truncated_max_points");
        json.RootElement.GetProperty("timeline")[0].GetProperty("eventType").GetString()
            .Should().Be("case.created");
    }

    [Fact]
    public void Support_Message_Page_Uses_Stable_Cursor_And_Nested_Actor_MessageType_CdnRef()
    {
        var caseId = Guid.Parse("489660be-7844-42bc-a48f-f5c707b85b25");
        var messages = new[]
        {
            Message(caseId, Guid.Parse("10000000-0000-0000-0000-000000000001"), BaseTime, "message",
                attachments: new[] { Attachment(caseId, Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    "support_attachment/document-1.pdf") }),
            Message(caseId, Guid.Parse("10000000-0000-0000-0000-000000000002"), BaseTime.AddMinutes(1), "reply"),
            Message(caseId, Guid.Parse("10000000-0000-0000-0000-000000000003"), BaseTime.AddMinutes(2), "message"),
        };

        var first = CaseCursorPagination.Messages(messages, null, 2);
        var second = CaseCursorPagination.Messages(messages, first.NextCursor, 2);
        var response = new CaseMessagePageResponseV2
        { Items = first.Items, Total = first.Total, NextCursor = first.NextCursor };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        first.Items.Select(item => item.MessageId).Should().Equal(
            new[] { messages[1].MessageId, messages[2].MessageId },
            "the first page selects the newest window and returns it chronologically");
        first.NextCursor.Should().NotBeNullOrWhiteSpace();
        second.Items.Should().ContainSingle().Which.MessageId.Should().Be(messages[0].MessageId);
        second.NextCursor.Should().BeNull();
        var item = json.RootElement.GetProperty("items")[0];
        item.GetProperty("messageType").GetString().Should().Be("reply");
        item.GetProperty("actor").GetProperty("ref").GetString().Should().Be("client-1");
        item.GetProperty("actor").GetProperty("role").GetString().Should().Be("client");
        using var earlierJson = JsonDocument.Parse(JsonSerializer.Serialize(
            new CaseMessagePageResponseV2 { Items = second.Items, Total = second.Total },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        earlierJson.RootElement.GetProperty("items")[0].GetProperty("attachments")[0]
            .GetProperty("cdnRef").GetString().Should().Be("support_attachment/document-1.pdf");
    }

    [Fact]
    public void Support_List_Keyset_Does_Not_Duplicate_When_A_Newer_Case_Arrives()
    {
        var original = new[]
        {
            Case(Guid.Parse("30000000-0000-0000-0000-000000000003"), "support", BaseTime.AddMinutes(3)),
            Case(Guid.Parse("30000000-0000-0000-0000-000000000002"), "support", BaseTime.AddMinutes(2)),
            Case(Guid.Parse("30000000-0000-0000-0000-000000000001"), "support", BaseTime.AddMinutes(1)),
        };
        var first = CaseCursorPagination.Cases(original, null, 2);
        var withNewArrival = original.Prepend(
            Case(Guid.Parse("30000000-0000-0000-0000-000000000004"), "support", BaseTime.AddMinutes(4))).ToArray();

        var second = CaseCursorPagination.Cases(withNewArrival, first.NextCursor, 2);

        first.Items.Select(item => item.CaseId).Should().Equal(original[0].CaseId, original[1].CaseId);
        second.Items.Should().ContainSingle().Which.CaseId.Should().Be(original[2].CaseId);
        first.Items.Select(item => item.CaseId).Intersect(second.Items.Select(item => item.CaseId)).Should().BeEmpty();
    }

    [Fact]
    public void Reply_Attachments_Remain_ObjectRef_String_List()
    {
        var request = new CaseReplyRequestV2
        {
            Body = "More evidence",
            ExpectedVersion = 4,
            Attachments = new[] { "support_attachment/document-1.pdf" },
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        json.RootElement.GetProperty("expectedVersion").GetInt64().Should().Be(4);
        json.RootElement.GetProperty("attachments")[0].ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("attachments")[0].GetString()
            .Should().Be("support_attachment/document-1.pdf");
    }

    private static GenericCaseV1 Case(Guid id, string kind, DateTimeOffset createdAt) => new()
    {
        CaseId = id, Kind = kind, Category = "general",
        Subject = new GenericCaseSubjectV1 { Type = kind == "dispute" ? "delivery" : "account", Ref = "subject-1" },
        RequesterRef = "client-1", Status = kind == "dispute" ? "pending" : "open",
        Priority = "normal", Version = 3, CreatedAt = createdAt, UpdatedAt = createdAt,
    };

    private static GenericCaseMessageV1 Message(Guid caseId, Guid messageId,
        DateTimeOffset createdAt, string messageType, string body = "Message body",
        IReadOnlyList<GenericCaseAttachmentV1>? attachments = null) => new()
    {
        MessageId = messageId, CaseId = caseId, MessageType = messageType, Body = body,
        Actor = new GenericCaseActorV1 { Ref = "client-1", Role = "client" },
        CaseVersion = 2, CreatedAt = createdAt,
        Attachments = attachments ?? Array.Empty<GenericCaseAttachmentV1>(),
    };

    private static GenericCaseAttachmentV1 Attachment(Guid caseId, Guid? messageId, string cdnRef) => new()
    {
        AttachmentId = Guid.NewGuid(), CaseId = caseId, MessageId = messageId,
        CdnRef = cdnRef, AddedBy = "client-1", CreatedAt = BaseTime,
    };
}
