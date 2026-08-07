using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Disputes;
using JeebGateway.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class DisputeSupportEndpointContractTests
{
    private static readonly Guid CaseId = Guid.Parse("489660be-7844-42bc-a48f-f5c707b85b25");

    [Fact]
    public async Task Public_Dispute_Create_Requires_Caller_Key_And_Accepts_Five_Photos_Plus_Voice()
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);
        var client = Client(factory, "client-1", "customer");
        var missing = await client.PostAsJsonAsync(
            "/v1/disputes", new { deliveryId = "delivery-1", reason = "damaged" });
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.DefaultRequestHeaders.Add("Idempotency-Key", "five-plus-voice");
        var photos = Enumerable.Range(1, 5)
            .Select(index => $"dispute_evidence/photo-{index}.jpg").ToArray();
        var created = await client.PostAsJsonAsync("/v1/disputes", new
        {
            deliveryId = "delivery-1", reason = "damaged",
            attachments = photos.Append("dispute_evidence/voice.m4a"),
            voiceUrl = "dispute_evidence/voice.m4a",
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        cases.Created!.Attachments.Should().Equal(photos);
        cases.Created.VoiceUrl.Should().Be("dispute_evidence/voice.m4a");
        cases.Created.IdempotencyKey.Should().Be("five-plus-voice");
    }

    [Fact]
    public async Task Active_Dispute_Conflict_Preserves_Case_Id_And_Kind_Aliases()
    {
        var cases = new FakeCases { ActiveConflict = true };
        using var factory = Factory(cases);
        var client = Client(factory, "client-1", "customer");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "active-conflict");
        var response = await client.PostAsJsonAsync(
            "/v1/disputes", new { deliveryId = "delivery-1", reason = "damaged" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("existingCaseId").GetString().Should().Be(CaseId.ToString("D"));
        json.RootElement.GetProperty("caseId").GetString().Should().Be(CaseId.ToString("D"));
        json.RootElement.GetProperty("kind").GetString().Should().Be("dispute");
    }

    [Fact]
    public async Task Evidence_Preview_Uses_Canonical_Public_Route()
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);
        var response = await Client(factory, "courier-1", "driver")
            .GetAsync("/v1/deliveries/delivery-1/disputes/evidence-preview");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        cases.Preview.Should().Be(("delivery-1", "courier-1", "jeeber"));
    }

    [Fact]
    public async Task Admin_Dispute_Alias_Rejects_Support_Kind_And_Any_Cod_RefundUsd()
    {
        var cases = new FakeCases { Kind = GenericCaseKinds.Support };
        using var factory = Factory(cases);
        var admin = Client(factory, "admin-1", "admin");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", "admin-case-key");
        var wrongKind = await admin.PostAsJsonAsync(
            $"/admin/v1/disputes/{CaseId:D}/claim", new { expectedVersion = 4 });
        wrongKind.StatusCode.Should().Be(HttpStatusCode.NotFound);
        cases.Patches.Should().BeEmpty();

        cases.Kind = GenericCaseKinds.Dispute;
        var refund = await admin.PostAsJsonAsync(
            $"/admin/v1/disputes/{CaseId:D}/resolve",
            new { action = "fixed", refundUsd = 1.0m, expectedVersion = 4 });
        refund.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        cases.Patches.Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_Case_Evidence_Uses_Case_Scoped_Opaque_Paths_Without_Raw_Cdn_Refs()
    {
        const string rawReference = "dispute_evidence/private-proof.jpg";
        var cases = new FakeCases
        {
            EvidenceRef = rawReference,
            InternalNote = CaseApiProjection.MetadataBody(new CaseGatewayMetadataV1
            {
                VoiceUrl = rawReference,
                Evidence = new[]
                {
                    new GenericCaseEvidenceV1
                    {
                        Source = "cdn",
                        Status = "complete",
                        CapturedAt = DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
                        Payload = JsonSerializer.SerializeToElement(new { cdnRef = rawReference }),
                    },
                },
            }),
        };
        using var factory = Factory(cases);
        var admin = Client(factory, "admin-1", "admin");

        var response = await admin.GetAsync($"/admin/v1/cases/{CaseId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(rawReference);
        body.Should().NotContain(CaseApiProjection.MetadataPrefix);
        using var json = JsonDocument.Parse(body);
        var path = json.RootElement.GetProperty("attachments")[0].GetString();
        path.Should().StartWith($"/gateway/admin/v1/cases/{CaseId:D}/evidence/");
        var token = path!.Split('/').Last();

        var otherCase = Guid.Parse("589660be-7844-42bc-a48f-f5c707b85b25");
        var crossCase = await admin.GetAsync($"/admin/v1/cases/{otherCase:D}/evidence/{token}");
        crossCase.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_Message_Page_Removes_Synthetic_Metadata_Without_Dropping_Internal_Notes_Or_Cursor()
    {
        const string rawReference = "dispute_evidence/private-page-proof.jpg";
        var cases = new FakeCases
        {
            Kind = GenericCaseKinds.Support,
            InternalNote = CaseApiProjection.MetadataBody(new CaseGatewayMetadataV1
            {
                VoiceUrl = rawReference,
                Evidence = new[]
                {
                    new GenericCaseEvidenceV1
                    {
                        Source = "cdn",
                        Status = "complete",
                        CapturedAt = DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
                        Payload = JsonSerializer.SerializeToElement(new { cdnRef = rawReference }),
                    },
                },
            }),
            LegitimateInternalNote = "finance review complete",
            MessagePageNextCursor = "owner-cursor-2",
        };
        using var factory = Factory(cases);
        var admin = Client(factory, "admin-1", "admin");

        var response = await admin.GetAsync($"/admin/v1/cases/{CaseId:D}/messages?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(CaseApiProjection.MetadataPrefix);
        body.Should().NotContain(rawReference);
        body.Should().Contain("finance review complete");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("nextCursor").GetString().Should().Be("owner-cursor-2");
        json.RootElement.GetProperty("items").EnumerateArray().Should()
            .Contain(message => message.GetProperty("body").GetString() == "finance review complete");
    }

    [Fact]
    public async Task Only_Admin_Can_Close_A_Dispute()
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);
        var customer = Client(factory, "client-1", "customer");
        customer.DefaultRequestHeaders.Add("Idempotency-Key", "close-1");
        (await customer.PostAsJsonAsync(
            $"/admin/v1/disputes/{CaseId:D}/close", new { expectedVersion = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = Client(factory, "admin-1", "admin");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", "close-1");
        (await admin.PostAsJsonAsync(
            $"/admin/v1/disputes/{CaseId:D}/close", new { expectedVersion = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        cases.Patches.Should().ContainSingle().Which.Status.Should().Be(GenericCaseStatuses.Closed);
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("closed")]
    public async Task Legacy_Status_Only_Terminal_Retry_Returns_Current_Without_Another_Mutation(string action)
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);
        var admin = Client(factory, "admin-1", "admin");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", $"terminal-retry-{action}");
        var request = new { action, expectedVersion = 4 };

        var first = await admin.PostAsJsonAsync($"/admin/v1/disputes/{CaseId:D}/resolve", request);
        var retry = await admin.PostAsJsonAsync($"/admin/v1/disputes/{CaseId:D}/resolve", request);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.Headers.ETag!.Tag.Should().Be("\"5\"");
        cases.Patches.Should().ContainSingle().Which.Status.Should().Be(action);
        cases.StatusMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_Public_Dispute_Never_Projects_Internal_Note_As_Resolution()
    {
        var cases = new FakeCases { InternalNote = "private admin reasoning" };
        using var factory = Factory(cases);
        var response = await Client(factory, "client-1", "customer")
            .GetAsync($"/disputes/{CaseId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("private admin reasoning");
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("resolution", out var resolution))
            resolution.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Legacy_Get_Is_Requester_Only_But_Admin_Can_Read_Any_Dispute()
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);

        (await Client(factory, "courier-1", "driver").GetAsync($"/disputes/{CaseId:D}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(factory, "admin-1", "admin").GetAsync($"/disputes/{CaseId:D}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Legacy_List_Paginates_And_Hydrates_Every_Requester_Record()
    {
        var cases = new FakeCases { LegacyRowCount = 205 };
        using var factory = Factory(cases);

        var response = await Client(factory, "client-1", "customer").GetAsync("/disputes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DisputeListResponse>();
        body!.Total.Should().Be(205);
        body.Items.Should().HaveCount(205);
        body.Items.Select(item => item.Id).Should().OnlyHaveUniqueItems();
        body.Items.Should().OnlyContain(item =>
            item.FiledByUserId == "client-1" && !string.IsNullOrWhiteSpace(item.Description));
        cases.ListQueries.Should().HaveCount(2);
        cases.ListQueries.Should().OnlyContain(query => query.RequesterRef == "client-1");
        cases.DetailReads.Should().Be(205);
    }

    [Fact]
    public async Task Legacy_Create_Preserves_Photo_Schemes_And_Description_Limit()
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);
        var client = Client(factory, "client-1", "customer");
        var description = new string('d', DisputeService.MaxDescriptionLength);

        var accepted = await client.PostAsJsonAsync("/deliveries/delivery-1/dispute", new
        {
            category = DisputeCategory.DamagedGoods,
            description,
            photoUrls = new[] { " S3://bucket/a.jpg ", "http://cdn.test/b.jpg", "HTTPS://cdn.test/c.jpg" },
        });
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        cases.Created!.Comment.Should().Be(description);
        cases.Created.Attachments.Should().Equal(
            "S3://bucket/a.jpg", "http://cdn.test/b.jpg", "HTTPS://cdn.test/c.jpg");

        (await client.PostAsJsonAsync("/deliveries/delivery-2/dispute", new
        {
            category = DisputeCategory.DamagedGoods,
            description = new string('d', DisputeService.MaxDescriptionLength + 1),
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/deliveries/delivery-3/dispute", new
        {
            category = DisputeCategory.DamagedGoods,
            description = "invalid evidence",
            photoUrls = new[] { "ftp://cdn.test/a.jpg" },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Legacy_Terminal_Resolution_Is_Atomic_Bounded_And_Retry_Reconciled()
    {
        var cases = new FakeCases();
        using var factory = Factory(cases);
        var admin = Client(factory, "admin-1", "admin");
        var resolution = new string('r', DisputeService.MaxResolutionLength);

        var first = await admin.PutAsJsonAsync($"/admin/disputes/{CaseId:D}/resolve", new
        {
            action = "resolve",
            resolution,
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        cases.StatusMessages.Should().ContainSingle().Which.Should().Be(
            (GenericCaseStatuses.Fixed, resolution));

        var retry = await admin.PutAsJsonAsync($"/admin/disputes/{CaseId:D}/resolve", new
        {
            action = "resolve",
            resolution,
        });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        cases.StatusMessages.Should().ContainSingle("the terminal state and public resolution already agree");

        (await admin.PutAsJsonAsync($"/admin/disputes/{CaseId:D}/resolve", new
        {
            action = "resolve",
            resolution = new string('r', DisputeService.MaxResolutionLength + 1),
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Support_Reply_Is_Singular_And_Reconciles_Body_Cas_With_IfMatch()
    {
        var cases = new FakeCases { Kind = GenericCaseKinds.Support };
        using var factory = Factory(cases);
        var client = Client(factory, "client-1", "customer");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "support-reply-1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", "\"4\"");
        var response = await client.PostAsJsonAsync(
            $"/v1/support/tickets/{CaseId:D}/reply",
            new { body = "Receipt attached", expectedVersion = 4,
                attachments = new[] { "support_attachment/receipt.pdf" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        cases.Message.Should().NotBeNull();
        cases.Message!.Value.Version.Should().Be(4);
        cases.Message.Value.Attachments.Should().Equal("support_attachment/receipt.pdf");

        client.DefaultRequestHeaders.Remove("If-Match");
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", "\"5\"");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "support-reply-mismatch");
        var mismatch = await client.PostAsJsonAsync(
            $"/v1/support/tickets/{CaseId:D}/reply",
            new { body = "mismatch", expectedVersion = 4 });
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static WebApplicationFactory<Program> Factory(IGenericCaseGatewayService cases) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AdminEvidence:TokenKey", "case-evidence-test-key-32-bytes-minimum");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDisputeService>();
                services.RemoveAll<IGenericCaseGatewayService>();
                services.AddSingleton(cases);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory, string user, string role)
    {
        var client = factory.CreateClient();
        if (string.Equals(role, "admin", StringComparison.Ordinal))
        {
            return client.WithBearer(
                CapabilityTestHarness.MintExternalOperatorBearer(factory, role));
        }
        client.DefaultRequestHeaders.Add("X-User-Id", user);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        return client;
    }

    private sealed class FakeCases : IGenericCaseGatewayService
    {
        private string _status = GenericCaseStatuses.Pending;
        private string? _publicResolution;
        private int _version = 4;

        public string Kind { get; set; } = GenericCaseKinds.Dispute;
        public bool ActiveConflict { get; init; }
        public string? InternalNote { get; init; }
        public string? LegitimateInternalNote { get; init; }
        public string? MessagePageNextCursor { get; init; }
        public string? EvidenceRef { get; init; }
        public int LegacyRowCount { get; init; } = 1;
        public int DetailReads { get; private set; }
        public CreateDisputeCaseInput? Created { get; private set; }
        public (string Delivery, string User, string Role)? Preview { get; private set; }
        public List<PatchGenericCaseRequestV1> Patches { get; } = new();
        public List<GenericCaseQueryV1> ListQueries { get; } = new();
        public List<(string Status, string Body)> StatusMessages { get; } = new();
        public (int Version, IReadOnlyList<string> Attachments)? Message { get; private set; }

        public Task<GenericCaseDetailV1> CreateDisputeAsync(CreateDisputeCaseInput input, CancellationToken ct)
        {
            if (ActiveConflict)
                throw new CaseConflictException(JsonSerializer.Serialize(new
                { existingCaseId = CaseId, kind = GenericCaseKinds.Dispute }));
            Created = input;
            return Task.FromResult(Detail());
        }

        public Task<GenericCaseDetailV1> CreateSupportAsync(CreateSupportCaseInput input, CancellationToken ct) =>
            Task.FromResult(Detail());
        public Task<GenericCaseDetailV1> GetForUserAsync(
            string caseId, string userId, bool isAdmin, CancellationToken ct)
        {
            DetailReads++;
            return Task.FromResult(Detail(ParseCaseId(caseId)));
        }
        public Task<GenericCaseDetailV1> GetForRequesterAsync(
            string caseId, string requesterRef, CancellationToken ct)
        {
            DetailReads++;
            var detail = Detail(ParseCaseId(caseId));
            return string.Equals(detail.Case.RequesterRef, requesterRef, StringComparison.Ordinal)
                ? Task.FromResult(detail)
                : Task.FromException<GenericCaseDetailV1>(new CaseAccessDeniedException());
        }
        public Task<GenericCasePageV1> ListForUserAsync(
            string kind, string userId, GenericCaseQueryV1 query, CancellationToken ct)
        {
            ListQueries.Add(query);
            return Task.FromResult(new GenericCasePageV1 { Items = new[] { Row() } });
        }
        public Task<GenericCasePageV1> ListForRequesterAsync(
            string kind, string requesterRef, GenericCaseQueryV1 query, CancellationToken ct)
        {
            ListQueries.Add(query);
            var offset = query.Cursor is null
                ? 0
                : int.Parse(query.Cursor["legacy-offset:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var take = Math.Min(query.Limit, LegacyRowCount - offset);
            var items = Enumerable.Range(offset, take).Select(index => Row(RowId(index))).ToArray();
            var nextOffset = offset + take;
            return Task.FromResult(new GenericCasePageV1
            {
                Items = items,
                NextCursor = nextOffset < LegacyRowCount ? $"legacy-offset:{nextOffset}" : null,
            });
        }
        public Task<GenericCasePageV1> ListAdminAsync(
            GenericCaseQueryV1 query, bool? unassigned, CancellationToken ct) =>
            Task.FromResult(new GenericCasePageV1 { Items = new[] { Row() } });
        public Task<GenericCaseMessagePageV1> ListMessagesForUserAsync(
            string caseId, string userId, bool isAdmin, int limit, string? cursor, CancellationToken ct) =>
            Task.FromResult(new GenericCaseMessagePageV1
            {
                Items = Detail(ParseCaseId(caseId)).Messages,
                NextCursor = MessagePageNextCursor,
            });
        public Task<DisputeEvidencePreviewResponseV1> PreviewDisputeEvidenceAsync(
            string deliveryId, string userId, string userRole, CancellationToken ct)
        {
            Preview = (deliveryId, userId, userRole);
            return Task.FromResult(new DisputeEvidencePreviewResponseV1
            { DeliveryId = deliveryId, Evidence = Array.Empty<GenericCaseEvidenceV1>() });
        }
        public Task<GenericCaseDetailV1> PatchAsync(string caseId, PatchGenericCaseRequestV1 patch,
            string actorId, string actorRole, string key, CancellationToken ct)
        {
            Patches.Add(patch);
            _status = patch.Status ?? _status;
            _version++;
            return Task.FromResult(Detail(ParseCaseId(caseId)));
        }
        public Task<GenericCaseDetailV1> ApplyStatusMessageAsync(string caseId, int expectedVersion,
            string status, string body, string actorId, string actorRole, string key, CancellationToken ct)
        {
            StatusMessages.Add((status, body));
            _status = status;
            _publicResolution = body;
            _version++;
            return Task.FromResult(Detail(ParseCaseId(caseId)));
        }
        public Task<GenericCaseDetailV1> AddMessageAsync(string caseId, int expectedVersion,
            string messageType, string actorId, string actorRole, string key, string? body,
            Guid? replyToId, IReadOnlyList<string>? attachments, CancellationToken ct)
        {
            Message = (expectedVersion, attachments ?? Array.Empty<string>());
            return Task.FromResult(Detail());
        }
        public Task<GenericCaseDetailV1> ReopenAsync(string caseId, int expectedVersion,
            string actorId, string actorRole, string key, string? reason, CancellationToken ct) =>
            Task.FromResult(Detail());

        private GenericCaseDetailV1 Detail(Guid? caseId = null)
        {
            var id = caseId ?? CaseId;
            var messages = new List<GenericCaseMessageV1>
            {
                new GenericCaseMessageV1
                {
                    MessageId = MessageId(id, 1), CaseId = id, MessageType = "message",
                    Body = $"description-{id:D}",
                    Actor = new GenericCaseActorV1 { Ref = "client-1", Role = "client" },
                    CaseVersion = 1, CreatedAt = DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
                    Attachments = EvidenceRef is null
                        ? Array.Empty<GenericCaseAttachmentV1>()
                        : new[] { Attachment(id, MessageId(id, 1), EvidenceRef) },
                },
            };
            if (InternalNote is not null)
            {
                messages.Add(new GenericCaseMessageV1
                {
                    MessageId = MessageId(id, 2), CaseId = id, MessageType = "internal_note", Body = InternalNote,
                    Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
                    CaseVersion = 4, CreatedAt = DateTimeOffset.Parse("2026-08-05T10:00:00Z"),
                });
            }
            if (LegitimateInternalNote is not null)
            {
                messages.Add(new GenericCaseMessageV1
                {
                    MessageId = MessageId(id, 4), CaseId = id, MessageType = "internal_note",
                    Body = LegitimateInternalNote,
                    Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
                    CaseVersion = 4, CreatedAt = DateTimeOffset.Parse("2026-08-05T10:30:00Z"),
                });
            }
            if (_publicResolution is not null)
            {
                messages.Add(new GenericCaseMessageV1
                {
                    MessageId = MessageId(id, 3), CaseId = id, MessageType = "message", Body = _publicResolution,
                    Actor = new GenericCaseActorV1 { Ref = "admin-1", Role = "admin" },
                    CaseVersion = 5, CreatedAt = DateTimeOffset.Parse("2026-08-05T11:00:00Z"),
                });
            }
            return new GenericCaseDetailV1
            {
                Case = Row(id),
                Messages = messages,
                Attachments = EvidenceRef is null
                    ? Array.Empty<GenericCaseAttachmentV1>()
                    : new[] { Attachment(id, null, EvidenceRef) },
            };
        }

        private static GenericCaseAttachmentV1 Attachment(Guid caseId, Guid? messageId, string cdnRef) => new()
        {
            AttachmentId = Guid.Parse("689660be-7844-42bc-a48f-f5c707b85b25"),
            CaseId = caseId,
            MessageId = messageId,
            CdnRef = cdnRef,
            AddedBy = "client-1",
            CreatedAt = DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
        };

        private GenericCaseV1 Row(Guid? caseId = null) => new()
        {
            CaseId = caseId ?? CaseId, Kind = Kind, Category = "damaged",
            Subject = new GenericCaseSubjectV1 { Type = "delivery", Ref = "delivery-1" },
            RequesterRef = "client-1", ParticipantRefs = new[] { "client-1", "courier-1" },
            Status = _status, Priority = GenericCasePriorities.Normal, Version = _version,
            CreatedAt = DateTimeOffset.Parse("2026-08-05T09:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-05T10:00:00Z"),
        };

        private static Guid ParseCaseId(string value) =>
            Guid.Parse(value.StartsWith("dsp_", StringComparison.Ordinal) ? value[4..] : value);

        private static Guid RowId(int index) => index == 0
            ? CaseId
            : new Guid(index, 0, 0, new byte[8]);

        private static Guid MessageId(Guid caseId, byte suffix)
        {
            var bytes = caseId.ToByteArray();
            bytes[^1] = suffix;
            return new Guid(bytes);
        }
    }
}
