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

public sealed class NotificationRecordWriterTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task OfferReceived_WireIsConcreteAndTyped_AndAcceptsCommittedStatuses(
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
    public async Task AC6a_AC6b_AC6c_AC6d_Ambiguous500_CommitsAfterOneReadBackWithoutErrorAndPushes()
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
        push.Sends.Should().Be(1);
    }

    [Fact]
    public async Task AC7a_AC7b_AC7c_AC7d_Ambiguous500_MissIsUnprovenLogsAndPushesWithoutRetry()
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
        push.Sends.Should().Be(1);
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

    private static NotificationRecordWriter NewWriter(
        RecordingHandler handler,
        RecordingLogger<NotificationRecordWriter>? logger = null,
        bool enabled = true)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
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
        public List<bool> ObservedCancellation { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedCancellation.Add(cancellationToken.IsCancellationRequested);
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
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

    private sealed class CapturingNotificationRecordWriter : INotificationRecordWriter
    {
        private readonly INotificationRecordWriter _inner;

        public CapturingNotificationRecordWriter(INotificationRecordWriter inner)
        {
            _inner = inner;
        }

        public NotificationRecordWriteOutcome? LastOutcome { get; private set; }

        public async Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
            OfferReceivedNotificationRecord record,
            CancellationToken requestToken)
        {
            LastOutcome = await _inner.WriteOfferReceivedAsync(record, requestToken);
            return LastOutcome;
        }

        public Task<NotificationRecordWriteOutcome> WriteOfferAcceptedAsync(
            OfferAcceptedNotificationRecord record,
            CancellationToken requestToken)
            => throw new NotSupportedException();
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
