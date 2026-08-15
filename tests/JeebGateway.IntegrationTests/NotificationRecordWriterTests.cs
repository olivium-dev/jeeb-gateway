using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Notifications;
using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

[Collection("FM1 notification durability telemetry")]
public sealed class NotificationRecordWriterTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task AC8a_AC9a_OfferReceived_2xxPostsExactlyOnceWithConcreteNumbers(
        HttpStatusCode postStatus)
    {
        var handler = new RecordingHandler(postStatus);
        var writer = NewWriter(handler);
        var record = ReceivedRecord();

        var outcome = await writer.WriteOfferReceivedAsync(record, CancellationToken.None);

        outcome.Classification.Should().Be(NotificationRecordWriteClassification.Committed);
        handler.Posts.Should().Be(1);
        handler.Gets.Should().Be(0);
        var json = JsonDocument.Parse(handler.PostBodies.Single()).RootElement;
        json.GetProperty("senderProfilePicture").GetString().Should().BeEmpty();
        json.GetProperty("nickname").GetString().Should().BeEmpty();
        json.GetProperty("notification_id").GetString().Should().Be(record.NotificationCorrelationId);
        var payload = json.GetProperty("payload");
        payload.GetProperty("offer_amount").ValueKind.Should().Be(JsonValueKind.Number);
        payload.GetProperty("offer_amount").GetDecimal().Should().Be(12.5m);
        payload.GetProperty("delivery_fee").ValueKind.Should().Be(JsonValueKind.Number);
        payload.GetProperty("delivery_fee").GetDecimal().Should().Be(12.5m);
        payload.GetProperty("pickup_location").GetString().Should().Be("Hamra, Beirut");
        handler.PostBodies.Single().Should().NotContain("valueKind");
        handler.PostBodies.Single().Should().NotContain("\"url\":[]");
    }

    [Fact]
    public async Task AC6a_AC6b_AC6c_AC6d_AC9b_Ambiguous500_CommitsAfterOnePostAndReadBack_CentreOwnsThePush()
    {
        var record = ReceivedRecord();
        var notificationId = NotificationCorrelationId.Create(
            OfferReceivedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.OfferId);
        var handler = new RecordingHandler(
            HttpStatusCode.InternalServerError,
            readBody:
                $$"""{"messages":[{"notification_id":"{{notificationId}}"}],"total_messages":1}""");
        var logger = new RecordingLogger<NotificationRecordWriter>();
        var writer = new CapturingNotificationRecordWriter(NewWriter(handler, logger));
        var push = new RecordingPushClient();
        var notifier = NewNotifier(writer, push);

        await notifier.NotifyNewOfferAsync(
            ReceivedContext(),
            record.Receiver,
            "request-1",
            record.Payload.OfferId,
            record.Payload.OfferAmount,
            CancellationToken.None);

        writer.LastOutcome!.Classification.Should()
            .Be(NotificationRecordWriteClassification.CommittedAfterAmbiguousResponse);
        handler.Posts.Should().Be(1, "the notification service does not deduplicate NCIDs");
        handler.Gets.Should().Be(1);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Information &&
            Equals(
                entry.Properties["classification"],
                "committed_after_ambiguous_response"));
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
        push.Sends.Should().Be(
            0, "the read-back PROVED the row upstream, so notification-service already pushed it");
    }

    [Fact]
    public async Task AC7a_AC7b_AC7c_AC7d_AC9c_Ambiguous500_MissIsUnproven_AndIsNotResentDirectly()
    {
        long unprovenCount = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName &&
                instrument.Name == "notif.durable_write.outcomes")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, state) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "classification" &&
                        Equals(tag.Value, "unproven"))
                    {
                        Interlocked.Add(ref unprovenCount, measurement);
                    }
                }
            });
        meterListener.Start();

        var handler = new RecordingHandler(
            HttpStatusCode.InternalServerError,
            readBody: """{"messages":[],"total_messages":0}""");
        var logger = new RecordingLogger<NotificationRecordWriter>();
        var writer = new CapturingNotificationRecordWriter(NewWriter(handler, logger));
        var push = new RecordingPushClient();
        var notifier = NewNotifier(writer, push);

        var act = async () => await notifier.NotifyNewOfferAsync(
            ReceivedContext(),
            "client-1",
            "request-1",
            "offer-1",
            12.5m,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        writer.LastOutcome!.Classification.Should()
            .Be(NotificationRecordWriteClassification.Unproven);
        handler.Posts.Should().Be(1);
        handler.Gets.Should().Be(1);
        var error = logger.Entries.Should()
            .ContainSingle(entry => entry.Level == LogLevel.Error)
            .Subject;
        error.Properties["event"].Should().Be("notif.durable_write.failed");
        error.Properties["classification"].Should().Be("unproven");
        unprovenCount.Should().Be(1);
        push.Sends.Should().Be(
            0,
            "unproven means the POST went out and the read-back could not prove the row ABSENT — "
            + "sending anyway is how one offer became two cards on 2026-08-14. The loss is "
            + "monitored (Error log + the unproven counter asserted above), not papered over");
    }

    [Fact]
    public async Task TransportFaults_NeverThrow_AndNeverRetryPost()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            throwPost: true,
            throwGet: true);
        var writer = NewWriter(handler);

        NotificationRecordWriteOutcome? outcome = null;
        var act = async () =>
        {
            outcome = await writer.WriteOfferAcceptedAsync(
                AcceptedRecord(),
                CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
        outcome!.Classification.Should().Be(NotificationRecordWriteClassification.Unproven);
        handler.Posts.Should().Be(1);
        handler.Gets.Should().Be(1);
    }

    [Fact]
    public async Task CallerCancellation_IsNotPropagatedToPostCommitAttempt()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        var outcome = await writer.WriteOfferAcceptedAsync(AcceptedRecord(), caller.Token);

        outcome.Classification.Should().Be(NotificationRecordWriteClassification.Committed);
        handler.ObservedCancellation.Should().ContainSingle().Which.Should().BeFalse(
            "the post-commit durability budget is independent of request cancellation");
    }

    [Fact]
    public async Task DisabledFlag_SkipsOnlyWriterTraffic()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler, enabled: false);

        var outcome = await writer.WriteOfferReceivedAsync(
            ReceivedRecord(),
            CancellationToken.None);

        outcome.Classification.Should().Be(NotificationRecordWriteClassification.Disabled);
        handler.Posts.Should().Be(0);
        handler.Gets.Should().Be(0);
    }

    // ── b02 step 3 · the silent gate, AFTER the 2026-07-27 delivery reversal ──────────
    //
    // WHAT CHANGED, AND WHAT WAS LOST. Until 2026-07-27 this section held a real
    // positive/negative control pair: `jeeb.delivery_status_updated` (silent, zero POSTs)
    // against `jeeb.settlement_paid` (stored, one POST), differing in exactly one input.
    // The owner then ruled that a delivery-status change IS a readable inbox row, so
    // `delivery` moved to shade+stored — and with it, the LAST silent-classified catalog
    // template key disappeared. `newRequest` is still silent but has no template key and no
    // centre writer.
    //
    // Consequence, stated plainly rather than hidden behind a green suite: the silent
    // branch of NotificationRecordWriter.WriteAsync is now UNREACHABLE for every real
    // notification type, so its runtime behaviour (skip-before-POST, the SkippedSilent
    // classification, the `notif.durable_write.skipped` record and its `durableWriteEnabled`
    // tag) is no longer exercised here. The four tests that used to exercise it were removed
    // rather than kept alive against a fabricated silent key: PushSilencePolicy.IsSilent is
    // a pure static lookup, so the only way to feed it a silent key would be to invent one
    // the catalog does not serve — a faked positive control, which is precisely the failure
    // this batch keeps being burned by.
    //
    // The gate CODE stays. It is a dormant guard sitting at the sole choke point, and
    // Step3_TheSilentGate_IsDormantAndMustStaySo below is the tripwire that makes the day a
    // silent type reappears the day someone has to restore this coverage.

    /// <summary>
    /// The 2026-07-27 reversal proved at the writer choke point, not just in the policy
    /// table: <c>jeeb.delivery_status_updated</c> now POSTs exactly one row. This is the
    /// assertion that inverted — it read "zero POSTs, SkippedSilent" before the ruling.
    /// </summary>
    [Fact]
    public async Task Reversal20260727_DeliveryStatusUpdated_WritesExactlyOneRow()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler, out var http);
        var postDelegateInvocations = 0;

        var outcome = await writer.WriteAsync(
            DeliveryTemplateKey,
            "client-1",
            "delivery-1",
            "ncid-delivery",
            ct =>
            {
                postDelegateInvocations++;
                return PostCentreRowAsync(http, DeliveryTemplateKey, ct);
            });

        outcome.Classification.Should().Be(
            NotificationRecordWriteClassification.Committed,
            "owner ruling 2026-07-27: delivery is a readable inbox row, so the gate must "
            + "let it through — SkippedSilent here means the reversal was undone");
        postDelegateInvocations.Should().Be(1);
        handler.Posts.Should().Be(1, "shade+stored ⇒ exactly one centre row, never two");
        handler.PostPaths.Should().ContainSingle()
            .Which.Should().Be($"/notifications/{DeliveryTemplateKey}");
    }

    /// <summary>
    /// The tripwire for the dormancy described above. It is deliberately an assertion about
    /// the policy rather than about the writer, because there is nothing left to drive
    /// through the writer: if this goes red, a silent catalog type exists again, the gate is
    /// live again, and the writer-level skip tests deleted on 2026-07-27 must come back with
    /// it. The reference to <see cref="NotificationRecordWriteClassification.SkippedSilent"/>
    /// is load-bearing too — deleting the gate's classification becomes a compile error here
    /// rather than a silently vanished test.
    /// </summary>
    [Fact]
    public void Step3_TheSilentGate_IsDormantAndMustStaySo()
    {
        PushSilencePolicy.TemplateKeys.Where(PushSilencePolicy.IsSilent).Should().BeEmpty(
            "after the 2026-07-27 delivery reversal no catalog notification type is silent; "
            + "if you have added one, restore the writer-level skip coverage that proved "
            + "the gate refuses the POST, records notif.durable_write.skipped, and outranks "
            + "the durable-write feature flag");

        Enum.IsDefined(NotificationRecordWriteClassification.SkippedSilent).Should().BeTrue(
            "the gate is dormant, not deleted");
    }

    [Fact]
    public async Task Step3Negative_NonSilentType_WritesExactlyOneRow()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler, out var http);
        var postDelegateInvocations = 0;

        var outcome = await writer.WriteAsync(
            NonSilentTemplateKey,
            "client-1",
            "delivery-1",
            "ncid-silent",
            ct =>
            {
                postDelegateInvocations++;
                return PostCentreRowAsync(http, NonSilentTemplateKey, ct);
            });

        outcome.Classification.Should().Be(NotificationRecordWriteClassification.Committed);
        postDelegateInvocations.Should().Be(1);
        handler.Posts.Should().Be(1, "non-silent ⇒ exactly one centre row");
        handler.PostPaths.Should().ContainSingle()
            .Which.Should().Be($"/notifications/{NonSilentTemplateKey}");
        handler.Gets.Should().Be(0);
    }

    // REMOVED 2026-07-27, with the delivery reversal — see the section header above:
    //   Step3_SilentType_StaysSkippedSilent_WhenTheDurableWriteFlagIsOff
    //   Step3_SilentSkipRecord_ReportsTheDurableWriteFlagAsOn_WhenItIsOn
    // They asserted that the silent gate outranks the durable-write feature flag and that
    // the `notif.durable_write.skipped` record carries `durableWriteEnabled` in both
    // states. Both drove the writer with `jeeb.delivery_status_updated`, the only silent
    // catalog key there was; the ruling made it stored, and no other silent key exists to
    // put in its place. The production code they covered is intact and unchanged.
    // Step3_TheSilentGate_IsDormantAndMustStaySo goes red the moment a silent type returns,
    // which is the moment to restore both of these from git history
    // (`git log -S Step3_SilentSkipRecord_ReportsTheDurableWriteFlagAsOn_WhenItIsOn`).
    // The flag-off half of the ordering claim still has live coverage in
    // CentreWritersStep6Tests.Step6a_DisabledFlag_IsReportedAsDisabledNotAsSilent.

    [Fact]
    public async Task Step3_LiveWriters_StayNonSilent_AndStillPostExactlyOnce()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var writer = NewWriter(handler);

        var received = await writer.WriteOfferReceivedAsync(
            ReceivedRecord(),
            CancellationToken.None);
        var accepted = await writer.WriteOfferAcceptedAsync(
            AcceptedRecord(),
            CancellationToken.None);

        received.Classification.Should().Be(NotificationRecordWriteClassification.Committed);
        accepted.Classification.Should().Be(NotificationRecordWriteClassification.Committed);
        handler.Posts.Should().Be(2, "one row each — the gate must not silence these");
        handler.PostPaths.Should().BeEquivalentTo(new[]
        {
            "/notifications/jeeb.offer_received",
            "/notifications/jeeb.offer_accepted",
        });
    }

    // Was `SilentTemplateKey` until the 2026-07-27 reversal made it stored. Renamed rather
    // than repointed at another key: there is no other silent key, and leaving the old name
    // on a stored type is how a reader concludes the gate is still being exercised.
    private const string DeliveryTemplateKey = "jeeb.delivery_status_updated";
    private const string NonSilentTemplateKey = "jeeb.settlement_paid";

    // Mirrors JeebNotificationRecordClient.PostAsync's route shape
    // (POST notifications/{templateKey}) for a type that has no typed writer yet, so the
    // assertion is about a real request on the same handler, not a mock counter.
    private static async Task<HttpStatusCode> PostCentreRowAsync(
        HttpClient http,
        string templateKey,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            $"notifications/{templateKey}",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            cancellationToken);
        return response.StatusCode;
    }

    private static NotificationRecordWriter NewWriter(
        RecordingHandler handler,
        out HttpClient http,
        RecordingLogger<NotificationRecordWriter>? logger = null,
        bool enabled = true)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
        return NewWriter(http, logger, enabled);
    }

    private static NotificationRecordWriter NewWriter(
        RecordingHandler handler,
        RecordingLogger<NotificationRecordWriter>? logger = null,
        bool enabled = true)
        => NewWriter(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") },
            logger,
            enabled);

    private static NotificationRecordWriter NewWriter(
        HttpClient http,
        RecordingLogger<NotificationRecordWriter>? logger,
        bool enabled)
    {
        var client = new JeebNotificationRecordClient(http);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NotificationRecordWriter.EnabledConfigurationKey] = enabled.ToString(),
            })
            .Build();
        return new NotificationRecordWriter(
            client,
            configuration,
            logger ?? new RecordingLogger<NotificationRecordWriter>());
    }

    private static OfferPushNotifier NewNotifier(
        INotificationRecordWriter writer,
        RecordingPushClient push)
        => new(
            push,
            writer,
            (_, _) => Task.FromResult<DeliveryRequest?>(null),
            NullLogger<OfferPushNotifier>.Instance);

    private static OfferReceivedNotificationContext ReceivedContext()
        => new(
            "Hamra, Beirut",
            "Achrafieh, Beirut",
            30,
            DateTimeOffset.Parse("2026-07-26T10:11:12Z"));

    private static OfferReceivedNotificationRecord ReceivedRecord() => new()
    {
        Sender = "jeeb-gateway",
        Receiver = "client-1",
        NotificationCorrelationId = "11111111-2222-4333-8444-555555555555",
        Title = "New offer on your request",
        Description = "You received a new offer for $12.5. Tap to review.",
        Payload = new OfferReceivedNotificationPayload
        {
            UserId = "client-1",
            OfferId = "offer-1",
            ClientName = string.Empty,
            PickupLocation = "Hamra, Beirut",
            DeliveryLocation = "Achrafieh, Beirut",
            OfferAmount = 12.5m,
            DeliveryFee = 12.5m,
            EstimatedDuration = "30",
            CreatedAt = DateTimeOffset.Parse("2026-07-26T10:11:12Z"),
        },
    };

    private static OfferAcceptedNotificationRecord AcceptedRecord() => new()
    {
        Sender = "jeeb-gateway",
        Receiver = "jeeber-1",
        NotificationCorrelationId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
        Title = "Offer Accepted",
        Description = "Your delivery offer has been accepted.",
        Payload = new OfferAcceptedNotificationPayload
        {
            UserId = "client-1",
            OfferId = "offer-1",
            ClientName = string.Empty,
            PickupLocation = "A",
            DeliveryLocation = "B",
            AcceptedAmount = 12.5m,
            JeeberId = "jeeber-1",
            CreatedAt = DateTimeOffset.Parse("2026-07-26T10:11:12Z"),
        },
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _postStatus;
        private readonly string _readBody;
        private readonly bool _throwPost;
        private readonly bool _throwGet;

        public RecordingHandler(
            HttpStatusCode postStatus,
            string readBody = """{"messages":[],"total_messages":0}""",
            bool throwPost = false,
            bool throwGet = false)
        {
            _postStatus = postStatus;
            _readBody = readBody;
            _throwPost = throwPost;
            _throwGet = throwGet;
        }

        public int Posts { get; private set; }
        public int Gets { get; private set; }
        public List<string> PostBodies { get; } = new();

        /// <summary>
        /// Absolute paths of every POST seen. The step-3 DoD is stated in terms of
        /// <c>POST /notifications/*</c>, so the trace is asserted on the PATH, not on a
        /// bare counter that a refactor could satisfy for the wrong reason.
        /// </summary>
        public List<string> PostPaths { get; } = new();
        public List<bool> ObservedCancellation { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedCancellation.Add(cancellationToken.IsCancellationRequested);
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                PostPaths.Add(request.RequestUri!.AbsolutePath);
                if (request.Content is not null)
                {
                    PostBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                }
                if (_throwPost)
                {
                    throw new HttpRequestException("post transport fault");
                }
                return new HttpResponseMessage(_postStatus);
            }

            Gets++;
            if (_throwGet)
            {
                throw new HttpRequestException("get transport fault");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_readBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CapturingNotificationRecordWriter : FakeNotificationRecordWriterBase
    {
        private readonly INotificationRecordWriter _inner;

        public CapturingNotificationRecordWriter(INotificationRecordWriter inner)
        {
            _inner = inner;
        }

        public NotificationRecordWriteOutcome? LastOutcome { get; private set; }

        public override async Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
            OfferReceivedNotificationRecord record,
            CancellationToken requestToken)
        {
            LastOutcome = await _inner.WriteOfferReceivedAsync(record, requestToken);
            return LastOutcome;
        }
    }

    private sealed class RecordingPushClient : ServicePushNotificationClient
    {
        public RecordingPushClient() : base("http://127.0.0.1/", new HttpClient())
        {
        }

        public int Sends { get; private set; }

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id,
            SentPayloadToUserRequest body,
            CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(new SentPayloadResponse
            {
                Message = "ok",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>
                ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add(new LogEntry(
                logLevel,
                properties.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        IReadOnlyDictionary<string, object?> Properties);
}
