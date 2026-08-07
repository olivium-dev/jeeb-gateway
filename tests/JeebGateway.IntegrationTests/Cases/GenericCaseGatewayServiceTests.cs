using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class GenericCaseGatewayServiceTests
{
    [Fact]
    public async Task Dispute_Uses_Canonical_Parties_And_Cod_Pending_Contract()
    {
        var state = new FakeCaseStateClient();
        var delivery = new DeliveryHandler();
        var detail = await Service(state, delivery).CreateDisputeAsync(Input("client-1"), default);

        detail.Case.Status.Should().Be("pending");
        detail.Case.Version.Should().Be(3, "create, initial message, and metadata are CAS mutations");
        state.Created!.Kind.Should().Be("dispute");
        state.Created.Category.Should().Be("damaged");
        state.Created.Subject.Should().BeEquivalentTo(new { Type = "delivery", Ref = "delivery-1" });
        state.Created.RequesterRef.Should().Be("client-1");
        state.Created.ParticipantRefs.Should().Equal("client-1", "courier-1");
        state.Created.Attachments!.Select(item => item.CdnRef)
            .Should().ContainSingle("dispute_evidence/object-1");
        state.Messages.Should().Contain(item => item.MessageType == "internal_note"
            && item.Body!.StartsWith(CaseApiProjection.MetadataPrefix, StringComparison.Ordinal));
        delivery.TransitionBodies.Should().BeEmpty(
            "a complaint cannot mutate delivery without the explicit incident command");
    }

    [Fact]
    public async Task Canonical_Status_History_Parties_Are_The_Authorization_Source()
    {
        var state = new FakeCaseStateClient();
        var act = () => Service(state, new DeliveryHandler())
            .CreateDisputeAsync(Input("stranger"), default);

        await act.Should().ThrowAsync<CaseAccessDeniedException>();
        state.Created.Should().BeNull();
    }

    [Fact]
    public async Task Dispute_List_Uses_Subject_Scope_For_A_Canonical_Counterparty()
    {
        var state = new FakeCaseStateClient();
        var service = Service(state, new DeliveryHandler());
        await service.CreateDisputeAsync(Input("client-1"), default);

        var page = await service.ListForUserAsync(GenericCaseKinds.Dispute, "courier-1",
            new GenericCaseQueryV1
            {
                SubjectRef = "delivery-1", Limit = 200, Sort = GenericCaseSorts.Sla,
            }, default);

        page.Items.Should().ContainSingle().Which.CaseId.Should().Be(FakeCaseStateClient.CaseId);
        state.ListQueries.Should().Contain(query => query.ParticipantRef == "courier-1"
            && query.SubjectType == "delivery" && query.SubjectRef == "delivery-1"
            && query.Sort == GenericCaseSorts.Recent);
        state.ListQueries.Should().HaveCount(1, "participantRefs include requesters after state migration");
    }

    [Fact]
    public async Task Dispute_List_Without_Subject_Is_Requester_Scoped_And_Does_Not_Fan_Out()
    {
        var state = new FakeCaseStateClient();
        var service = Service(state, new DeliveryHandler());
        await service.CreateDisputeAsync(Input("client-1"), default);

        await service.ListForUserAsync(GenericCaseKinds.Dispute, "client-1",
            new GenericCaseQueryV1 { Limit = 200, Sort = GenericCaseSorts.Sla }, default);

        state.ListQueries.Should().Contain(query => query.ParticipantRef == "client-1"
            && query.RequesterRef == null && query.SubjectRef == null
            && query.Sort == GenericCaseSorts.Recent);
        state.ListQueries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Explicit_Requester_List_Does_Not_Use_Delivery_Participants()
    {
        var state = new FakeCaseStateClient();
        var service = Service(state, new DeliveryHandler());
        await service.CreateDisputeAsync(Input("client-1"), default);

        await service.ListForRequesterAsync(GenericCaseKinds.Dispute, "client-1",
            new GenericCaseQueryV1 { Limit = 200 }, default);

        var query = state.ListQueries.Should().ContainSingle().Subject;
        query.RequesterRef.Should().Be("client-1");
        query.ParticipantRef.Should().BeNull();
    }

    [Theory]
    [InlineData(DeliveryReadMode.Missing)]
    [InlineData(DeliveryReadMode.Reassigned)]
    [InlineData(DeliveryReadMode.Outage)]
    public async Task Persisted_Requester_Reads_Survive_Missing_Reassigned_Or_Unavailable_Delivery(
        DeliveryReadMode mode)
    {
        var state = new FakeCaseStateClient();
        state.SeedClosed();
        var delivery = new DeliveryHandler { ReadMode = mode };
        var service = Service(state, delivery);

        var detail = await service.GetForRequesterAsync(
            FakeCaseStateClient.CaseId.ToString("D"), "client-1", default);
        var page = await service.ListForRequesterAsync(GenericCaseKinds.Dispute, "client-1",
            new GenericCaseQueryV1 { Limit = 20 }, default);

        detail.Case.RequesterRef.Should().Be("client-1");
        page.Items.Should().ContainSingle();
        delivery.StatusReads.Should().Be(0, "legacy filer reads authorize from persisted requesterRef only");
    }

    [Theory]
    [InlineData(DeliveryReadMode.Missing, typeof(CaseNotFoundException))]
    [InlineData(DeliveryReadMode.Reassigned, typeof(CaseAccessDeniedException))]
    [InlineData(DeliveryReadMode.Outage, typeof(HttpRequestException))]
    public async Task Participant_Detail_Retains_Live_Delivery_Authorization(
        DeliveryReadMode mode, Type expectedException)
    {
        var state = new FakeCaseStateClient();
        state.SeedClosed();
        var delivery = new DeliveryHandler { ReadMode = mode };
        var action = () => Service(state, delivery).GetForUserAsync(
            FakeCaseStateClient.CaseId.ToString("D"), "client-1", false, default);

        var thrown = await action.Should().ThrowAsync<Exception>();

        thrown.Which.Should().BeOfType(expectedException);
        delivery.StatusReads.Should().Be(1);
    }

    [Fact]
    public async Task Five_Photos_And_A_Separate_Voice_Object_Are_Accepted()
    {
        var state = new FakeCaseStateClient();
        var photos = Enumerable.Range(1, 5)
            .Select(index => $"dispute_evidence/photo-{index}.jpg").ToArray();
        var input = new CreateDisputeCaseInput
        {
            DeliveryId = "delivery-1", UserId = "client-1", UserRole = "client",
            Reason = "damaged", Attachments = photos,
            VoiceUrl = "dispute_evidence/voice.m4a", IdempotencyKey = "five-plus-voice",
        };

        await Service(state, new DeliveryHandler()).CreateDisputeAsync(input, default);

        state.Created!.Attachments.Should().HaveCount(6);
        state.Created.Attachments!.Select(item => item.CdnRef)
            .Should().Contain(photos).And.Contain("dispute_evidence/voice.m4a");
    }

    [Fact]
    public void Mobile_Attachment_Alias_Removes_The_Separately_Declared_Voice_Object()
    {
        var request = new CreateDisputeRequestV2
        {
            Attachments = Enumerable.Range(1, 5)
                .Select(index => $"dispute_evidence/photo-{index}.jpg")
                .Append("dispute_evidence/voice.m4a").ToArray(),
            VoiceUrl = "dispute_evidence/voice.m4a",
        };

        request.ResolveAttachments().Should().HaveCount(5)
            .And.NotContain("dispute_evidence/voice.m4a");
    }

    [Fact]
    public async Task Evidence_Preview_Uses_Canonical_Membership_And_Collector()
    {
        var evidence = new CountingEvidence();
        var preview = await Service(new FakeCaseStateClient(), new DeliveryHandler(), evidence)
            .PreviewDisputeEvidenceAsync("delivery-1", "courier-1", "jeeber", default);

        preview.DeliveryId.Should().Be("delivery-1");
        preview.Evidence.Should().ContainSingle(item => item.Source == "gps_pings");
        evidence.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Public_Create_Rejects_A_Non_End_User_Role_Even_For_A_Delivery_Party()
    {
        var state = new FakeCaseStateClient();
        var input = new CreateDisputeCaseInput
        {
            DeliveryId = "delivery-1", UserId = "client-1", UserRole = "admin",
            Reason = "damaged", IdempotencyKey = "admin-create",
        };

        var action = () => Service(state, new DeliveryHandler()).CreateDisputeAsync(input, default);

        await action.Should().ThrowAsync<CaseAccessDeniedException>();
        state.Created.Should().BeNull();
    }

    [Fact]
    public async Task Selected_Incident_Uses_Deterministic_Delivery_Command_Through_Gateway_Client()
    {
        var state = new FakeCaseStateClient();
        var delivery = new DeliveryHandler();
        var input = Clone(Input("client-1"), GenericCaseGatewayService.ActivateIncidentCommand);

        var detail = await Service(state, delivery).CreateDisputeAsync(input, default);

        detail.Case.Version.Should().Be(4);
        using var command = JsonDocument.Parse(delivery.TransitionBodies.Single());
        command.RootElement.GetProperty("to").GetString().Should().Be("FailedNeedsEscalation");
        command.RootElement.GetProperty("idempotency_key").GetString()
            .Should().Be($"case:{FakeCaseStateClient.CaseId:D}:incident:activate");
    }

    [Fact]
    public async Task Selected_Incident_Failure_Preserves_Durable_Create_And_Is_Audited_Once()
    {
        var state = new FakeCaseStateClient();
        var input = Clone(Input("client-1"), GenericCaseGatewayService.ActivateIncidentCommand);
        var delivery = new DeliveryHandler { FailTransition = true };
        var evidence = new CountingEvidence();
        var service = Service(state, delivery, evidence);

        var created = await service.CreateDisputeAsync(input, default);
        var replayed = await service.CreateDisputeAsync(input, default);

        created.Case.Version.Should().Be(4,
            "the durable case plus safe secondary-failure audit are returned instead of a false 502");
        replayed.Case.Version.Should().Be(4);
        evidence.Calls.Should().Be(1, "persisted metadata makes evidence capture replay-safe");
        state.Messages.Count(item => item.Body?.StartsWith(CaseApiProjection.MetadataPrefix,
            StringComparison.Ordinal) == true).Should().Be(1);
        state.Messages.Should().ContainSingle(item => item.Body != null
            && item.Body.Contains("\"type\":\"delivery_incident_activation_failed\"", StringComparison.Ordinal));
        delivery.TransitionBodies.Should().ContainSingle(
            "an exact create replay must not repeat an optional incident command that already failed durably");
    }

    [Fact]
    public async Task Selected_Incident_Upstream_Timeout_Still_Returns_Durable_Create()
    {
        var state = new FakeCaseStateClient();
        var delivery = new DeliveryHandler { TimeoutTransition = true };
        var service = Service(state, delivery, new CountingEvidence());

        var created = await service.CreateDisputeAsync(
            Clone(Input("client-1"), GenericCaseGatewayService.ActivateIncidentCommand), default);

        created.Case.Version.Should().Be(4);
        state.Messages.Should().ContainSingle(item => item.Body != null
            && item.Body.Contains("\"type\":\"delivery_incident_activation_failed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_Replay_Resumes_Persisted_Steps_Without_Recapturing_Evidence()
    {
        var state = new FakeCaseStateClient();
        var evidence = new CountingEvidence();
        var service = Service(state, new DeliveryHandler(), evidence);

        var first = await service.CreateDisputeAsync(Input("client-1"), default);
        var replay = await service.CreateDisputeAsync(Input("client-1"), default);

        first.Case.Version.Should().Be(3);
        replay.Case.Version.Should().Be(3);
        evidence.Calls.Should().Be(1);
        state.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reopen_Closed_Case_Creates_Linked_Replacement_Because_State_Closes_Immutably()
    {
        var state = new FakeCaseStateClient();
        state.SeedClosed();

        var detail = await Service(state, new DeliveryHandler()).ReopenAsync(
            FakeCaseStateClient.CaseId.ToString("D"), 7, "admin-1", "admin",
            "reopen-1", "customer called back", default);

        detail.Case.CaseId.Should().Be(FakeCaseStateClient.ReopenedCaseId);
        detail.Case.Status.Should().Be("pending");
        detail.Case.Version.Should().Be(4);
        state.Created!.Subject.Should().BeEquivalentTo(new { Type = "delivery", Ref = "delivery-1" });
        state.Messages.Last().MessageType.Should().Be("internal_note");
        state.Messages.Last().Body.Should().Contain(FakeCaseStateClient.CaseId.ToString("D"));
        var projected = CaseApiProjection.Project(detail, includeInternal: false);
        projected.Subject.Should().Be("Original delivery issue");
        projected.Comment.Should().Be("Original opening body");
        projected.TicketNumber.Should().Be("DSP-ORIGINAL");
        projected.VoiceUrl.Should().Be("dispute_evidence/original-voice.m4a");
        projected.IncidentCommand.Should().Be(GenericCaseGatewayService.ActivateIncidentCommand);
        projected.Evidence.Should().ContainSingle().Which.Marker.Should().Be("original-route");
        projected.Photos.Should().Equal("dispute_evidence/original-photo.jpg");
        projected.Attachments.Should().BeEquivalentTo(
            "dispute_evidence/original-photo.jpg", "dispute_evidence/original-voice.m4a");
        state.MessageKeys.TakeLast(3).Should().Equal(
            GenericCaseGatewayService.DeterministicKey("reopen-1", "reopen-opening"),
            GenericCaseGatewayService.DeterministicKey("reopen-1", "reopen-metadata"),
            GenericCaseGatewayService.DeterministicKey("reopen-1", "reopen-link"));
    }

    [Theory]
    [InlineData("reopen-metadata")]
    [InlineData("reopen-link")]
    public async Task Reopen_Closed_Case_Resumes_Copied_Context_Without_Duplicating_Steps(
        string failingStep)
    {
        var state = new FakeCaseStateClient();
        state.SeedClosed();
        state.FailMessageKeyOnce = GenericCaseGatewayService.DeterministicKey("reopen-retry", failingStep);
        var service = Service(state, new DeliveryHandler());

        var first = () => service.ReopenAsync(FakeCaseStateClient.CaseId.ToString("D"), 7,
            "admin-1", "admin", "reopen-retry", "retry me", default);
        await first.Should().ThrowAsync<HttpRequestException>();

        var retried = await service.ReopenAsync(FakeCaseStateClient.CaseId.ToString("D"), 7,
            "admin-1", "admin", "reopen-retry", "retry me", default);

        retried.Case.Version.Should().Be(4);
        state.MessageKeys.Should().OnlyHaveUniqueItems();
        state.MessageKeys.Count(key => key == GenericCaseGatewayService.DeterministicKey(
            "reopen-retry", "reopen-opening")).Should().Be(1);
        state.MessageKeys.Count(key => key == GenericCaseGatewayService.DeterministicKey(
            "reopen-retry", "reopen-metadata")).Should().Be(1);
        state.MessageKeys.Count(key => key == GenericCaseGatewayService.DeterministicKey(
            "reopen-retry", "reopen-link")).Should().Be(1);
        CaseApiProjection.Project(retried, false).Comment.Should().Be("Original opening body");
    }

    [Fact]
    public async Task Support_Ticket_Number_Is_Derived_From_Case_Id_Not_Process_Time()
    {
        var state = new FakeCaseStateClient();

        await Service(state, new DeliveryHandler()).CreateSupportAsync(new CreateSupportCaseInput
        {
            UserId = "client-1", UserRole = "client", Category = "account",
            Body = "Please update my profile", IdempotencyKey = "support-1",
        }, default);

        state.Created!.ParticipantRefs.Should().Equal("client-1");
        state.Messages.Last().Body.Should().Contain("SUP-489660BE");
    }

    [Fact]
    public async Task Reply_ObjectRefs_Map_To_Opaque_State_Attachments()
    {
        var state = new FakeCaseStateClient();
        var service = Service(state, new DeliveryHandler());
        var created = await service.CreateSupportAsync(new CreateSupportCaseInput
        {
            UserId = "client-1", UserRole = "client", Category = "account",
            Body = "Initial question", IdempotencyKey = "support-attachments-1",
        }, default);

        await service.AddMessageAsync(created.Case.CaseId.ToString("D"), created.Case.Version,
            "message", "client-1", "client", "support-reply-1", "Receipt attached", null,
            new[] { "support_attachment/receipt-1.pdf" }, default);

        state.Messages.Last().Attachments.Should().ContainSingle()
            .Which.CdnRef.Should().Be("support_attachment/receipt-1.pdf");
    }

    [Fact]
    public async Task Support_Message_List_Uses_One_Bounded_State_Page_Without_Loading_The_Thread()
    {
        var state = new FakeCaseStateClient();
        state.SeedSupport();
        var page = await Service(state, new DeliveryHandler()).ListMessagesForUserAsync(
            FakeCaseStateClient.CaseId.ToString("D"), "client-1", false, 25, "state-cursor", default);

        page.NextCursor.Should().Be("earlier-cursor");
        state.MessagePageCalls.Should().ContainSingle().Which.Should().Be((25, "state-cursor", false));
        state.LegacyMessageReads.Should().Be(0,
            "the incremental endpoint must not materialize the complete support thread");
    }

    [Fact]
    public async Task Detail_Merges_Opening_Message_With_Newest_199_Without_Exceeding_200()
    {
        var state = new FakeCaseStateClient();
        state.SeedSupport();
        state.SeedMessages(205);

        var detail = await Service(state, new DeliveryHandler()).GetForUserAsync(
            FakeCaseStateClient.CaseId.ToString("D"), "client-1", false, default);

        detail.Messages.Should().HaveCount(200);
        detail.Messages.First().Body.Should().Be("message-1");
        detail.Messages.Skip(1).Select(message => message.Body)
            .Should().Equal(Enumerable.Range(7, 199).Select(index => $"message-{index}"));
        state.OrderedMessagePageCalls.Should().Contain((200, (string?)null, true, GenericCaseMessageOrders.Newest));
        state.OrderedMessagePageCalls.Should().Contain((20, (string?)null, true, GenericCaseMessageOrders.Oldest));
    }

    [Fact]
    public async Task Detail_Preserves_Second_Metadata_Message_And_Newest_Conversation_After_200_Replies()
    {
        var state = new FakeCaseStateClient();
        state.SeedSupport();
        state.SeedMessagesWithMetadata(205);

        var detail = await Service(state, new DeliveryHandler()).GetForUserAsync(
            FakeCaseStateClient.CaseId.ToString("D"), "client-1", false, default);
        var projected = CaseApiProjection.Project(detail, includeInternal: false);

        detail.Messages.Should().HaveCount(200);
        projected.ParticipantRefs.Should().Equal("client-1");
        detail.Messages.Should().ContainSingle(message =>
            message.Body.StartsWith(CaseApiProjection.MetadataPrefix, StringComparison.Ordinal));
        detail.Messages.First().Body.Should().Be("opening-description");
        detail.Messages.Skip(2).Select(message => message.Body)
            .Should().Equal(Enumerable.Range(8, 198).Select(index => $"reply-{index}"));
        projected.Subject.Should().Be("Metadata survives");
        projected.Body.Should().Be("opening-description");
    }

    [Fact]
    public async Task Status_And_Public_Message_Use_One_Atomic_State_Command()
    {
        var state = new FakeCaseStateClient();
        state.SeedSupport();

        var detail = await Service(state, new DeliveryHandler()).ApplyStatusMessageAsync(
            FakeCaseStateClient.CaseId.ToString("D"), 401, GenericCaseStatuses.Fixed,
            "Handled manually.", "admin-1", "admin", "atomic-status-1", default);

        detail.Case.Status.Should().Be(GenericCaseStatuses.Fixed);
        state.StatusMessages.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ExpectedVersion = 401,
            Status = GenericCaseStatuses.Fixed,
            Body = "Handled manually.",
        });
    }

    [Fact]
    public async Task Admin_Unassigned_Queue_Uses_State_Filter_Page_Size_And_Opaque_Cursor()
    {
        var state = new FakeCaseStateClient();
        state.SeedSupport();
        var page = await Service(state, new DeliveryHandler()).ListAdminAsync(
            new GenericCaseQueryV1
            {
                Kind = GenericCaseKinds.Support, Limit = 17, Cursor = "state-queue-cursor",
                Sort = GenericCaseSorts.Recent,
            }, unassigned: true, default);

        var query = state.ListQueries.Should().ContainSingle().Subject;
        query.Assigned.Should().BeFalse();
        query.Limit.Should().Be(17);
        query.Cursor.Should().Be("state-queue-cursor");
        query.Sort.Should().Be(GenericCaseSorts.Recent);
        page.NextCursor.Should().Be("next-state-cursor");
    }

    [Fact]
    public async Task Admin_Queue_Defaults_To_Sla_Sort_When_Caller_Omits_Sort()
    {
        var state = new FakeCaseStateClient();
        state.SeedSupport();

        await Service(state, new DeliveryHandler()).ListAdminAsync(
            new GenericCaseQueryV1 { Query = "delivery-42", Limit = 20 },
            unassigned: null, default);

        var query = state.ListQueries.Should().ContainSingle().Subject;
        query.Query.Should().Be("delivery-42");
        query.Sort.Should().Be(GenericCaseSorts.Sla);
    }

    private static GenericCaseGatewayService Service(FakeCaseStateClient state, HttpMessageHandler handler,
        ICaseEvidenceCollector? evidence = null) => new(
        state, new CaseDeliveryClient(new HttpClient(handler) { BaseAddress = new Uri("https://delivery/") }),
        evidence ?? new FixedEvidence(), TimeProvider.System, NullLogger<GenericCaseGatewayService>.Instance);

    private static CreateDisputeCaseInput Input(string userId) => new()
    {
        DeliveryId = "delivery-1", UserId = userId, UserRole = "client", Reason = "damaged",
        Comment = "Parcel was crushed", Attachments = new[] { "dispute_evidence/object-1" },
        IdempotencyKey = "create-1",
    };

    private static CreateDisputeCaseInput Clone(CreateDisputeCaseInput input, string incident) => new()
    {
        DeliveryId = input.DeliveryId, UserId = input.UserId, UserRole = input.UserRole,
        Reason = input.Reason, Comment = input.Comment, Attachments = input.Attachments,
        IdempotencyKey = input.IdempotencyKey, IncidentCommand = incident,
    };

    private sealed class FixedEvidence : ICaseEvidenceCollector
    {
        public Task<IReadOnlyList<GenericCaseEvidenceV1>> CaptureAsync(string deliveryId,
            string viewerUserId, IReadOnlyList<string> refs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GenericCaseEvidenceV1>>(new[]
            { new GenericCaseEvidenceV1 { Source = "gps_pings", Status = "partial", Marker = "test" } });
    }

    private sealed class CountingEvidence : ICaseEvidenceCollector
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<GenericCaseEvidenceV1>> CaptureAsync(string deliveryId,
            string viewerUserId, IReadOnlyList<string> refs, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<GenericCaseEvidenceV1>>(new[]
            {
                new GenericCaseEvidenceV1
                {
                    Source = "gps_pings", Status = "partial", Marker = "test",
                    CapturedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z").AddMinutes(Calls),
                },
            });
        }
    }

    private sealed class FakeCaseStateClient : IGenericCaseStateClient
    {
        public static readonly Guid CaseId = Guid.Parse("489660be-7844-42bc-a48f-f5c707b85b25");
        public static readonly Guid ReopenedCaseId = Guid.Parse("a8103179-cf23-4a06-8405-d3f9d94eff77");
        public CreateGenericCaseRequestV1? Created { get; private set; }
        public List<GenericCaseQueryV1> ListQueries { get; } = new();
        public List<CreateGenericCaseMessageRequestV1> Messages { get; } = new();
        public List<string> MessageKeys { get; } = new();
        public List<ApplyGenericCaseStatusMessageRequestV1> StatusMessages { get; } = new();
        public List<(int Limit, string? Cursor, bool IncludeInternal)> MessagePageCalls { get; } = new();
        public List<(int Limit, string? Cursor, bool IncludeInternal, string Order)> OrderedMessagePageCalls { get; }
            = new();
        public int LegacyMessageReads { get; private set; }
        public string? FailMessageKeyOnce { get; set; }
        private GenericCaseV1? _case;
        private bool _messageFailureInjected;
        private readonly Dictionary<string, GenericCaseV1> _createResponses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GenericCaseMessageCreatedV1> _messageResponses = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, GenericCaseV1> _cases = new();
        private readonly Dictionary<Guid, List<GenericCaseMessageV1>> _caseMessages = new();
        private readonly Dictionary<Guid, IReadOnlyList<GenericCaseAttachmentV1>> _caseAttachments = new();

        public Task<GenericCaseV1> CreateCaseAsync(CreateGenericCaseRequestV1 body, string key,
            string actorRef, string actorRole, CancellationToken ct)
        {
            if (_createResponses.TryGetValue(key, out var replay))
                return Task.FromResult(replay);
            Created = body;
            var id = _cases.TryGetValue(CaseId, out var original) && original.ClosedAt is not null
                ? ReopenedCaseId : CaseId;
            _case = new GenericCaseV1
            {
                CaseId = id, Kind = body.Kind, Category = body.Category, Subject = body.Subject,
                RequesterRef = body.RequesterRef, ParticipantRefs = body.ParticipantRefs,
                Status = body.Status, Priority = body.Priority,
                Version = 1, CreatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            };
            _cases[id] = _case;
            _caseMessages.TryAdd(id, new List<GenericCaseMessageV1>());
            _caseAttachments[id] = (body.Attachments ?? Array.Empty<GenericCaseAttachmentCreateV1>())
                .Select((item, index) => new GenericCaseAttachmentV1
                {
                    AttachmentId = Guid.Parse($"30000000-0000-4000-8000-{index + 1:D12}"),
                    CaseId = id,
                    CdnRef = item.CdnRef,
                    FileName = item.FileName,
                    ContentType = item.ContentType,
                    SizeBytes = item.SizeBytes,
                    AddedBy = actorRef,
                }).ToArray();
            _createResponses[key] = _case;
            return Task.FromResult(_case);
        }

        public Task<GenericCaseMessageCreatedV1> AddCaseMessageAsync(Guid id,
            CreateGenericCaseMessageRequestV1 body, string key, string actorRef, string actorRole, CancellationToken ct)
        {
            if (_messageResponses.TryGetValue(key, out var replay))
                return Task.FromResult(replay);
            if (!_messageFailureInjected && string.Equals(FailMessageKeyOnce, key, StringComparison.Ordinal))
            {
                _messageFailureInjected = true;
                throw new HttpRequestException("injected message-step failure");
            }
            Messages.Add(body);
            MessageKeys.Add(key);
            var current = _cases[id];
            _case = Copy(current, body.ExpectedVersion + 1);
            _cases[id] = _case;
            var message = new GenericCaseMessageV1
            {
                MessageId = Guid.NewGuid(), CaseId = id, MessageType = body.MessageType,
                Body = body.Body ?? string.Empty,
                Actor = new GenericCaseActorV1 { Ref = actorRef, Role = actorRole },
                CaseVersion = _case.Version,
            };
            _caseMessages.GetValueOrDefault(id)?.Add(message);
            var response = new GenericCaseMessageCreatedV1
            { CaseVersion = _case.Version, Message = message };
            _messageResponses[key] = response;
            return Task.FromResult(response);
        }

        public Task<GenericCaseV1> GetCaseAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_cases[id]);
        public Task<GenericCasePageV1> ListCasesAsync(GenericCaseQueryV1 query, CancellationToken ct)
        {
            ListQueries.Add(query);
            return Task.FromResult(new GenericCasePageV1
            {
                Items = new[] { _case! }, NextCursor = "next-state-cursor",
            });
        }
        public Task<GenericCaseV1> PatchCaseAsync(Guid id, PatchGenericCaseRequestV1 body, string key,
            string actorRef, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenericCaseStatusMessageV1> ApplyCaseStatusMessageAsync(Guid id,
            ApplyGenericCaseStatusMessageRequestV1 body, string key,
            string actorRef, string actorRole, CancellationToken ct)
        {
            StatusMessages.Add(body);
            _case = Copy(_cases[id], body.ExpectedVersion + 1, body.Status);
            _cases[id] = _case;
            var message = new GenericCaseMessageV1
            {
                MessageId = Guid.Parse("20000000-0000-4000-8000-000000000001"),
                CaseId = id,
                MessageType = "message",
                Body = body.Body,
                Actor = new GenericCaseActorV1 { Ref = actorRef, Role = actorRole },
                CaseVersion = _case.Version,
            };
            return Task.FromResult(new GenericCaseStatusMessageV1 { Case = _case, Message = message });
        }
        public Task<IReadOnlyList<GenericCaseMessageV1>> GetCaseMessagesAsync(Guid id, bool includeInternal, CancellationToken ct)
        {
            LegacyMessageReads++;
            return Task.FromResult(MaterializedMessages(id, includeInternal));
        }
        private IReadOnlyList<GenericCaseMessageV1> MaterializedMessages(Guid id, bool includeInternal) =>
            _caseMessages.GetValueOrDefault(id, new List<GenericCaseMessageV1>())
                .Where(message => includeInternal || message.MessageType != "internal_note").ToArray();
        public Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(Guid id,
            bool includeInternal, int limit, string? cursor, CancellationToken ct) =>
            GetCaseMessagesPageAsync(id, includeInternal, GenericCaseMessageOrders.Newest, limit, cursor, ct);
        public Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(Guid id,
            bool includeInternal, string order, int limit, string? cursor, CancellationToken ct)
        {
            MessagePageCalls.Add((limit, cursor, includeInternal));
            OrderedMessagePageCalls.Add((limit, cursor, includeInternal, order));
            if (cursor == "state-cursor")
                return Task.FromResult(new GenericCaseMessagePageV1
                { Items = Array.Empty<GenericCaseMessageV1>(), NextCursor = "earlier-cursor" });
            var all = MaterializedMessages(id, includeInternal);
            var items = order == GenericCaseMessageOrders.Oldest
                ? all.Take(limit).ToArray()
                : all.TakeLast(limit).ToArray();
            return Task.FromResult(new GenericCaseMessagePageV1
            {
                Items = items,
            });
        }
        public Task<IReadOnlyList<GenericCaseAttachmentV1>> GetCaseAttachmentsAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_caseAttachments.GetValueOrDefault(
                id, Array.Empty<GenericCaseAttachmentV1>()));
        public Task<IReadOnlyList<GenericCaseAuditEventV1>> GetCaseAuditAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GenericCaseAuditEventV1>>(Array.Empty<GenericCaseAuditEventV1>());
        public Task<GenericCaseDeadLetterPageV1> GetCaseDeadLettersAsync(
            int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenericCaseDeadLetterRequeueV1> RequeueCaseDeadLetterAsync(
            Guid eventId, string key, string actorRef, CancellationToken ct) => throw new NotSupportedException();

        public void SeedClosed()
        {
            _case = new GenericCaseV1
            {
                CaseId = CaseId,
                Kind = GenericCaseKinds.Dispute,
                Category = "damaged",
                Subject = new GenericCaseSubjectV1 { Type = "delivery", Ref = "delivery-1" },
                RequesterRef = "client-1",
                ParticipantRefs = new[] { "client-1", "courier-1" },
                Status = GenericCaseStatuses.Closed,
                Priority = GenericCasePriorities.High,
                Version = 7,
                ClosedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
                CreatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-08-05T01:00:00Z"),
            };
            _cases[CaseId] = _case;
            var metadata = CaseApiProjection.MetadataBody(new CaseGatewayMetadataV1
            {
                Subject = "Original delivery issue",
                TicketNumber = "DSP-ORIGINAL",
                VoiceUrl = "dispute_evidence/original-voice.m4a",
                IncidentCommand = GenericCaseGatewayService.ActivateIncidentCommand,
                Evidence = new[]
                {
                    new GenericCaseEvidenceV1
                    {
                        Source = "gps_pings", Status = "partial", Marker = "original-route",
                    },
                },
            });
            _caseMessages[CaseId] = new List<GenericCaseMessageV1>
            {
                StoredMessage(CaseId, 1, "message", "Original opening body", "client-1", "client"),
                StoredMessage(CaseId, 2, "internal_note", metadata, "jeeb-gateway", "system"),
            };
            _caseAttachments[CaseId] = new[]
            {
                StoredAttachment(CaseId, 1, "dispute_evidence/original-photo.jpg"),
                StoredAttachment(CaseId, 2, "dispute_evidence/original-voice.m4a"),
            };
        }

        public void SeedSupport()
        {
            _case = new GenericCaseV1
            {
                CaseId = CaseId, Kind = GenericCaseKinds.Support, Category = "account",
                Subject = new GenericCaseSubjectV1 { Type = "account", Ref = "client-1" },
                RequesterRef = "client-1", ParticipantRefs = new[] { "client-1" },
                Status = GenericCaseStatuses.Open, Priority = GenericCasePriorities.Normal,
                Version = 401, CreatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z"),
            };
            _cases[CaseId] = _case;
            _caseMessages[CaseId] = new List<GenericCaseMessageV1>();
            _caseAttachments[CaseId] = Array.Empty<GenericCaseAttachmentV1>();
        }

        public void SeedMessages(int count)
        {
            Messages.Clear();
            Messages.AddRange(Enumerable.Range(1, count).Select(index =>
                new CreateGenericCaseMessageRequestV1
                {
                    ExpectedVersion = index,
                    MessageType = "message",
                    Body = $"message-{index}",
                }));
            _caseMessages[CaseId] = Messages.Select((item, index) => StoredMessage(
                CaseId, index + 2, item.MessageType, item.Body!, "actor", "system")).ToList();
        }

        public void SeedMessagesWithMetadata(int replyCount)
        {
            Messages.Clear();
            Messages.Add(new CreateGenericCaseMessageRequestV1
            {
                ExpectedVersion = 1,
                MessageType = "message",
                Body = "opening-description",
            });
            Messages.Add(new CreateGenericCaseMessageRequestV1
            {
                ExpectedVersion = 2,
                MessageType = "internal_note",
                Body = CaseApiProjection.MetadataBody(new CaseGatewayMetadataV1
                {
                    Subject = "Metadata survives",
                }),
            });
            Messages.AddRange(Enumerable.Range(1, replyCount).Select(index =>
                new CreateGenericCaseMessageRequestV1
                {
                    ExpectedVersion = index + 2,
                    MessageType = "message",
                    Body = $"reply-{index}",
                }));
            _caseMessages[CaseId] = Messages.Select((item, index) => StoredMessage(
                CaseId, index + 2, item.MessageType, item.Body!, "actor", "system")).ToList();
        }

        private static GenericCaseV1 Copy(GenericCaseV1 row, int version, string? status = null) => new()
        {
            CaseId = row.CaseId, Kind = row.Kind, Category = row.Category, Subject = row.Subject,
            RequesterRef = row.RequesterRef, ParticipantRefs = row.ParticipantRefs,
            Status = status ?? row.Status, Priority = row.Priority, AssigneeRef = row.AssigneeRef,
            DueAt = row.DueAt, ClosedAt = row.ClosedAt,
            Version = version, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
        };

        private static GenericCaseMessageV1 StoredMessage(
            Guid caseId, int version, string type, string body, string actorRef, string actorRole) => new()
        {
            MessageId = Guid.Parse($"10000000-0000-4000-8000-{version:D12}"),
            CaseId = caseId,
            MessageType = type,
            Body = body,
            Actor = new GenericCaseActorV1 { Ref = actorRef, Role = actorRole },
            CaseVersion = version,
            CreatedAt = DateTimeOffset.Parse("2026-08-05T00:00:00Z").AddMinutes(version),
        };

        private static GenericCaseAttachmentV1 StoredAttachment(Guid caseId, int index, string cdnRef) => new()
        {
            AttachmentId = Guid.Parse($"30000000-0000-4000-8000-{index:D12}"),
            CaseId = caseId,
            CdnRef = cdnRef,
            AddedBy = "client-1",
        };
    }

    public enum DeliveryReadMode
    {
        Normal,
        Missing,
        Reassigned,
        Outage,
    }

    private sealed class DeliveryHandler : HttpMessageHandler
    {
        public bool FailTransition { get; set; }
        public bool TimeoutTransition { get; set; }
        public DeliveryReadMode ReadMode { get; init; }
        public int StatusReads { get; private set; }
        public List<string> TransitionBodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/status-history"))
            {
                StatusReads++;
                if (ReadMode == DeliveryReadMode.Missing)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (ReadMode == DeliveryReadMode.Outage)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return Json(new
                {
                    delivery_id = "delivery-1",
                    party_ids = ReadMode == DeliveryReadMode.Reassigned
                        ? new { client_id = "replacement-client", courier_id = "replacement-courier" }
                        : new { client_id = "client-1", courier_id = "courier-1" },
                    current_status = "InTransit", status_history = Array.Empty<object>(),
                });
            }
            TransitionBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if (TimeoutTransition)
                throw new TaskCanceledException("delivery transition timed out");
            if (FailTransition)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{\"reason\":\"temporarily_unavailable\"}") };
            return Json(new
            {
                delivery_id = "delivery-1", status = "FailedNeedsEscalation",
                transition_id = "transition-1", transitioned_at = "2026-08-05T00:01:00Z",
            });
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
