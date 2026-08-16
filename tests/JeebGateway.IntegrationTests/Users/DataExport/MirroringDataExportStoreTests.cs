using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Migration;
using JeebGateway.Services.Cdn;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Users.DataExport;

// gwdbx W1-06 — MirroringDataExportStore dual-write + the G-20 encrypt-then-upload artifact
// pipeline. Charter cases: idempotency key, +72h dueAt, ciphertext-only cdn, consume-CAS race.
// W5-11 deleted StoreDurabilityGuard and PostgresDataExportStore; the guard-roster case went with them.
public class MirroringDataExportStoreTests
{
    private const string UserId = "u-export-1";

    // Plaintext markers that must never appear in the bytes cdn receives.
    private static readonly byte[] Plaintext =
        Encoding.UTF8.GetBytes("{\"phone\":\"+9613000123\",\"address\":\"Rue Verdun 12\"}");

    private static readonly TimeSpan Sla = TimeSpan.FromHours(72);
    private static readonly TimeSpan LinkValidity = TimeSpan.FromDays(7);
    private static readonly string ArtifactKey = Convert.ToBase64String(new byte[32]);

    // ----- dual-write: create leg -------------------------------------------

    [Fact]
    public async Task DualWriteLocalRead_Creates_The_Work_Item_With_The_Charter_Idempotency_Key()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out _, out _);

        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);

        upstream.Creates.Should().ContainSingle();
        upstream.Creates[0].IdempotencyKey.Should().Be("data-export:" + UserId,
            "G-15 — the mirror key is exactly data-export:<userId>, the key the W1-07 relay replays");
        upstream.Creates[0].Body.Application.Should().Be("jeeb-gateway");
        upstream.Creates[0].Body.Kind.Should().Be("data-export");
        upstream.Creates[0].Body.SubjectRef.Should().Be(UserId);
        row.Status.Should().Be(DataExportStatus.Queued);
    }

    [Fact]
    public async Task Work_Item_DueAt_Is_The_72h_Sla_Deadline()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out _, out _);

        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);

        (row.DueBy - row.RequestedAt).Should().Be(Sla, "the local SLA deadline is +72h");
        upstream.Creates[0].Body.DueAt.Should().Be(row.DueBy, "dueAt mirrors the same +72h deadline");
    }

    [Fact]
    public async Task Work_Item_Payload_Carries_No_User_Bytes()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out _, out _);

        await store.RequestAsync(UserId, DataExportFormat.Json, default);

        var payload = upstream.Creates[0].Body.Payload!.Value;
        payload.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "exportId", "format" });
    }

    [Fact]
    public async Task Local_Mode_Does_Not_Mirror()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "local", out var inner, out _, out _);

        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);

        upstream.Creates.Should().BeEmpty("the ladder still sits at 'local'; the flip is W1-08");
        (await inner.GetLatestForUserAsync(UserId, default))!.Id.Should().Be(row.Id);
    }

    [Fact]
    public async Task Upstream_Failure_Never_Fails_The_Export_Request()
    {
        var upstream = new ThrowingOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out var inner, out _, out _);

        var act = async () => await store.RequestAsync(UserId, DataExportFormat.Json, default);

        var row = (await act.Should().NotThrowAsync("the mirror is fail-open")).Subject;
        (await inner.GetLatestForUserAsync(UserId, default))!.Id.Should().Be(row.Id);
    }

    // ----- G-20: encrypt-then-upload ----------------------------------------

    [Fact]
    public async Task MarkReady_Uploads_Ciphertext_Only_And_A_Spot_Fetch_Returns_No_Plaintext()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out var cdn, out var objects);
        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
        var lease = upstream.Lease(UserId);

        var readyAt = DateTimeOffset.UtcNow;
        var token = await store.MarkReadyAsync(
            row.Id, Plaintext, "application/json", readyAt, LinkValidity, default);

        // Setup actually applied: the CAS complete matched the lease + version.
        var item = upstream.Latest(UserId)!;
        item.Status.Should().Be("completed", "the mirror completed the LEASED work item");
        item.ArtifactRef.Should().Be(cdn.Tickets.Single().ObjectRef);
        upstream.TokenHash(UserId).Should().Be(Sha256Hex(token));
        item.ArtifactExpiresAt.Should().Be(readyAt + LinkValidity);
        item.Version.Should().Be(lease.Version + 1);

        // Spot-fetch the object cdn actually stored.
        objects.Should().ContainSingle();
        var stored = objects[item.ArtifactRef!];
        stored.Should().NotEqual(Plaintext, "cdn must never hold the plaintext export");
        Contains(stored, "+9613000123").Should().BeFalse("no PII may survive in the uploaded bytes");
        Contains(stored, "Rue Verdun").Should().BeFalse("no PII may survive in the uploaded bytes");
        stored[0].Should().Be(DataExportArtifactCipher.EnvelopeVersion);
        NewCipher().Decrypt(stored).Should().Equal(Plaintext, "the gateway-held key decrypts it back");
    }

    [Fact]
    public async Task Artifact_ObjectRef_Is_Random_And_Never_User_Derived()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out var cdn, out _);
        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
        upstream.Lease(UserId);

        await store.MarkReadyAsync(row.Id, Plaintext, "application/json", DateTimeOffset.UtcNow, LinkValidity, default);

        var mint = cdn.Requests.Single();
        mint.Slot.Should().Be("data-export", "the prefix is the owner decision, never user-derived");
        mint.Extension.Should().MatchRegex(@"^\.[0-9a-f]{32}\.bin$", "the gateway mints 128 random bits");
        Serialize(mint).Should().NotContain(UserId).And.NotContain(row.Id);

        var objectRef = cdn.Tickets.Single().ObjectRef;
        objectRef.Should().StartWith("data-export/").And.NotContain(UserId).And.NotContain(row.Id);
        CdnDataExportArtifactUploader.IsUnguessable(objectRef).Should().BeTrue();
    }

    [Fact]
    public async Task MarkReady_Without_A_Leased_Work_Item_Uploads_Nothing()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out var cdn, out var objects);
        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);

        var token = await store.MarkReadyAsync(
            row.Id, Plaintext, "application/json", DateTimeOffset.UtcNow, LinkValidity, default);

        token.Should().NotBeNullOrEmpty("the local ready transition is authoritative and unaffected");
        upstream.Latest(UserId)!.Status.Should().Be("queued");
        cdn.Requests.Should().BeEmpty("no lease means no home for the objectRef; never orphan a ciphertext");
        objects.Should().BeEmpty();
    }

    [Fact]
    public void Unguessable_Rejects_A_User_Derived_Object_Name()
    {
        CdnDataExportArtifactUploader.IsUnguessable("data-export/u-export-1.bin").Should().BeFalse();
        CdnDataExportArtifactUploader.IsUnguessable("data-export/" + Guid.NewGuid().ToString("N") + ".bin")
            .Should().BeTrue();
    }

    [Fact]
    public void Cipher_Round_Trips_And_Rejects_A_Tampered_Envelope()
    {
        var cipher = NewCipher();
        var envelope = cipher.Encrypt(Plaintext);

        cipher.Decrypt(envelope).Should().Equal(Plaintext);

        envelope[^1] ^= 0xFF;
        var act = () => cipher.Decrypt(envelope);
        act.Should().Throw<CryptographicException>("AES-GCM authenticates the artifact");
    }

    // ----- consume CAS -------------------------------------------------------

    [Fact]
    public async Task Two_Concurrent_Downloads_Consume_Exactly_Once()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out _, out _);
        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
        upstream.Lease(UserId);
        var token = await store.MarkReadyAsync(
            row.Id, Plaintext, "application/json", DateTimeOffset.UtcNow, LinkValidity, default);
        upstream.Latest(UserId)!.Status.Should().Be("completed", "setup: the artifact is staged upstream");

        var start = new SemaphoreSlim(0);
        async Task<bool> Download()
        {
            await start.WaitAsync();
            var found = await store.GetByDownloadTokenAsync(token, DateTimeOffset.UtcNow, default);
            return found is not null && await store.MarkDeliveredAsync(found.Id, DateTimeOffset.UtcNow, default);
        }

        var racers = new[] { Task.Run(Download), Task.Run(Download) };
        start.Release(2);
        var results = await Task.WhenAll(racers);

        results.Count(won => won).Should().Be(1, "the single-use download link is consumed exactly once");
        upstream.ConsumeAttempts.Should().BeGreaterThanOrEqualTo(1, "the winner mirrored its consume");
        upstream.ConsumeWins.Should().Be(1, "the upstream CAS admits exactly one consumer");
        upstream.Latest(UserId)!.Status.Should().Be("consumed");
        upstream.TokenHash(UserId).Should().BeNull("consume clears the capability");
    }

    // Deterministic sibling of the race above: the LOSER used to clear the subject entry before
    // the winner read it, silently dropping the winner's upstream consume.
    [Fact]
    public async Task Losing_Download_Does_Not_Strip_The_Winners_Upstream_Consume()
    {
        var upstream = new CasOwnershipClient();
        WinnerParkingStore? gate = null;
        var store = NewStore(upstream, "dual-write-local-read", out _, out _, out _,
            inner => gate = new WinnerParkingStore(inner));
        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
        upstream.Lease(UserId);
        var token = await store.MarkReadyAsync(
            row.Id, Plaintext, "application/json", DateTimeOffset.UtcNow, LinkValidity, default);
        upstream.Latest(UserId)!.Status.Should().Be("completed", "setup: the artifact is staged upstream");

        // Both racers resolve the token before either delivers.
        var first = await store.GetByDownloadTokenAsync(token, DateTimeOffset.UtcNow, default);
        var second = await store.GetByDownloadTokenAsync(token, DateTimeOffset.UtcNow, default);
        first.Should().NotBeNull();
        second.Should().NotBeNull();

        var winner = Task.Run(() => store.MarkDeliveredAsync(first!.Id, DateTimeOffset.UtcNow, default));
        await gate!.WinnerParked.WaitAsync(TimeSpan.FromSeconds(5));
        var loserWon = await store.MarkDeliveredAsync(second!.Id, DateTimeOffset.UtcNow, default);
        gate.ReleaseWinner();

        (await winner).Should().BeTrue("setup: the parked racer really is the local winner");
        loserWon.Should().BeFalse("the local single-use CAS admits exactly one downloader");
        upstream.ConsumeAttempts.Should().Be(1, "the loser must not erase the winner's consume");
        upstream.Latest(UserId)!.Status.Should().Be("consumed");
        upstream.TokenHash(UserId).Should().BeNull("a spent token leaves no live upstream capability");
    }

    [Fact]
    public async Task Consume_Cas_Rejects_A_Second_Consumer_At_The_Same_Version()
    {
        var upstream = new CasOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _, out _, out _);
        var row = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
        upstream.Lease(UserId);
        var token = await store.MarkReadyAsync(
            row.Id, Plaintext, "application/json", DateTimeOffset.UtcNow, LinkValidity, default);
        var item = upstream.Latest(UserId)!;

        var body = new WorkConsumeRequestV1
        {
            Application = "jeeb-gateway",
            DownloadTokenHash = Sha256Hex(token),
            ExpectedVersion = item.Version,
        };
        var attempts = await Task.WhenAll(
            Task.Run(() => TryConsume(upstream, item.WorkItemId, body)),
            Task.Run(() => TryConsume(upstream, item.WorkItemId, body)));

        attempts.Count(ok => ok).Should().Be(1, "version CAS admits one winner; the loser gets 409");
        upstream.ConsumeAttempts.Should().Be(2, "both racers really reached the CAS");
    }

    // ----- helpers -----------------------------------------------------------

    private static async Task<bool> TryConsume(
        IStateOwnershipClient upstream, Guid workItemId, WorkConsumeRequestV1 body)
    {
        try
        {
            await upstream.ConsumeWorkItemAsync(workItemId, body, default);
            return true;
        }
        catch (GenericCaseApiException)
        {
            return false;
        }
    }

    private static MirroringDataExportStore NewStore(
        IStateOwnershipClient upstream,
        string mode,
        out IDataExportStore inner,
        out RecordingCdnClient cdn,
        out ConcurrentDictionary<string, byte[]> objects,
        Func<IDataExportStore, IDataExportStore>? wrapInner = null)
    {
        var exportOptions = Options.Create(new DataExportOptions { Sla = Sla, LinkValidity = LinkValidity });
        inner = new InMemoryDataExportStore(TimeProvider.System, exportOptions);
        inner = wrapInner?.Invoke(inner) ?? inner;

        var store = new ObjectStore();
        objects = store.Objects;
        cdn = new RecordingCdnClient(store);

        var services = new ServiceCollection();
        services.AddSingleton(upstream);
        services.AddSingleton<ICDNServiceClient>(cdn);
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(store));
        services.AddSingleton(NewCipher());
        services.AddSingleton(Options.Create(new DataExportArtifactOptions { ArtifactKey = ArtifactKey }));
        services.AddScoped<IDataExportArtifactUploader, CdnDataExportArtifactUploader>();

        return new MirroringDataExportStore(
            inner,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<GwdbxMigrationOptions>(new GwdbxMigrationOptions { DataExportMode = mode }),
            NullLogger<MirroringDataExportStore>.Instance);
    }

    private static DataExportArtifactCipher NewCipher() =>
        new(Options.Create(new DataExportArtifactOptions { ArtifactKey = ArtifactKey }));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Serialize(CdnUploadUrlRequest request) => JsonSerializer.Serialize(request);

    private static bool Contains(byte[] haystack, string needle) =>
        Encoding.UTF8.GetString(haystack).Contains(needle, StringComparison.Ordinal)
        || haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle).AsSpan()) >= 0;

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Parks the local winner between the inner single-use CAS and the decorator's bookkeeping,
    // so the loser's whole leg provably runs first — no sleeps, no timing luck.
    private sealed class WinnerParkingStore : IDataExportStore
    {
        private readonly IDataExportStore _inner;
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WinnerParkingStore(IDataExportStore inner) => _inner = inner;

        public Task WinnerParked => _parked.Task;

        public void ReleaseWinner() => _release.TrySetResult();

        public async Task<bool> MarkDeliveredAsync(string exportId, DateTimeOffset now, CancellationToken ct)
        {
            var delivered = await _inner.MarkDeliveredAsync(exportId, now, ct);
            if (delivered)
            {
                _parked.TrySetResult();
                await _release.Task;
            }
            return delivered;
        }

        public Task<DataExportRequest> RequestAsync(string userId, string format, CancellationToken ct) =>
            _inner.RequestAsync(userId, format, ct);

        public Task<DataExportRequest?> GetLatestForUserAsync(string userId, CancellationToken ct) =>
            _inner.GetLatestForUserAsync(userId, ct);

        public Task<DataExportRequest?> GetByDownloadTokenAsync(
            string token, DateTimeOffset now, CancellationToken ct) =>
            _inner.GetByDownloadTokenAsync(token, now, ct);

        public Task<DataExportRequest?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct) =>
            _inner.ClaimNextAsync(now, ct);

        public Task<string> MarkReadyAsync(
            string exportId, byte[] payload, string contentType, DateTimeOffset now,
            TimeSpan linkValidity, CancellationToken ct) =>
            _inner.MarkReadyAsync(exportId, payload, contentType, now, linkValidity, ct);

        public Task MarkFailedAsync(string exportId, string reason, DateTimeOffset now, CancellationToken ct) =>
            _inner.MarkFailedAsync(exportId, reason, now, ct);
    }

    // The bytes cdn ends up holding, keyed by objectRef — the spot-fetch surface.
    private sealed class ObjectStore
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RecordingCdnClient : ICDNServiceClient
    {
        private readonly ObjectStore _store;

        public RecordingCdnClient(ObjectStore store) => _store = store;

        public List<CdnUploadUrlRequest> Requests { get; } = new();

        public List<CdnUploadTicket> Tickets { get; } = new();

        public Task<CdnUploadTicket> MintUploadUrlAsync(CdnUploadUrlRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            // Mirrors cdn-service ImageUploadController: {slot}/{guid:N}{extension}.
            var objectRef = $"{request.Slot}/{Guid.NewGuid():N}{request.Extension ?? ".bin"}";
            var ticket = new CdnUploadTicket
            {
                UploadUrl = "/" + CdnUploadUrlResolver.CdnPutSignedPathPrefix + objectRef + "?exp=1&ct=x&sig=y",
                ObjectRef = objectRef,
                ExpiresInSeconds = 300,
                Method = "PUT",
                RequiredHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/octet-stream" },
            };
            Tickets.Add(ticket);
            return Task.FromResult(ticket);
        }

        public Task<CdnAsset> UploadAsync(CdnUploadRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CdnSignedUrl> GetSignedUrlAsync(string assetId, int ttlSeconds, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CdnAsset?> GetAssetAsync(string assetId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly ObjectStore _store;

        public StubHttpClientFactory(ObjectStore store) => _store = store;

        public HttpClient CreateClient(string name) =>
            new(new CapturingHandler(_store)) { BaseAddress = new Uri("http://cdn.test/") };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly ObjectStore _store;

        public CapturingHandler(ObjectStore store) => _store = store;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var objectRef = path[(path.IndexOf(CdnUploadUrlResolver.CdnPutSignedPathPrefix, StringComparison.Ordinal)
                + CdnUploadUrlResolver.CdnPutSignedPathPrefix.Length)..];
            _store.Objects[objectRef] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // CAS-faithful stand-in for /work-items: complete needs lease + version, consume needs
    // application + token hash + version, both bump the version (state-service OwnershipStore).
    private sealed class CasOwnershipClient : IStateOwnershipClient
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, WorkItemRecordV1> _items = new(StringComparer.Ordinal);

        // WorkItemRecordV1 deliberately does not model downloadTokenHash (JsonIgnore upstream),
        // so the CAS keeps it beside the record exactly as the server column does.
        private readonly Dictionary<Guid, string?> _hashes = new();
        private int _consumeAttempts;
        private int _consumeWins;

        public List<(WorkItemCreateRequestV1 Body, string IdempotencyKey)> Creates { get; } = new();

        public int ConsumeAttempts => Volatile.Read(ref _consumeAttempts);

        public int ConsumeWins => Volatile.Read(ref _consumeWins);

        public WorkItemRecordV1? Latest(string subjectRef)
        {
            lock (_lock)
            {
                return _items.TryGetValue(subjectRef, out var item) ? item : null;
            }
        }

        public string? TokenHash(string subjectRef)
        {
            lock (_lock)
            {
                return _items.TryGetValue(subjectRef, out var item)
                    && _hashes.TryGetValue(item.WorkItemId, out var hash) ? hash : null;
            }
        }

        // Stands in for the W1-08 processor claim: queued -> leased with a lease token.
        public WorkItemRecordV1 Lease(string subjectRef)
        {
            lock (_lock)
            {
                var item = _items[subjectRef];
                var leased = Copy(item, status: "leased", version: item.Version + 1, leaseToken: Guid.NewGuid());
                _items[subjectRef] = leased;
                return leased;
            }
        }

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            lock (_lock)
            {
                Creates.Add((body, idempotencyKey));
                if (!_items.TryGetValue(body.SubjectRef, out var existing))
                {
                    existing = new WorkItemRecordV1
                    {
                        WorkItemId = Guid.NewGuid(),
                        Application = body.Application,
                        Kind = body.Kind,
                        SubjectRef = body.SubjectRef,
                        Status = "queued",
                        DueAt = body.DueAt ?? DateTimeOffset.UtcNow,
                        Version = 1,
                    };
                    _items[body.SubjectRef] = existing;
                }
                return Task.FromResult(existing);
            }
        }

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct) =>
            Task.FromResult(Latest(subjectRef));

        public Task<WorkItemRecordV1> CompleteWorkItemAsync(
            Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct)
        {
            lock (_lock)
            {
                var item = _items.Values.FirstOrDefault(i => i.WorkItemId == workItemId);
                if (item is null
                    || item.Status != "leased"
                    || item.LeaseToken != body.LeaseToken
                    || item.Version != body.ExpectedVersion)
                {
                    throw new GenericCaseApiException(409, "work.lease_conflict");
                }

                var completed = Copy(
                    item, status: "completed", version: item.Version + 1, leaseToken: null,
                    artifactRef: body.ArtifactRef, artifactExpiresAt: body.ArtifactExpiresAt);
                _items[item.SubjectRef] = completed;
                _hashes[item.WorkItemId] = body.DownloadTokenHash;
                return Task.FromResult(completed);
            }
        }

        public Task<WorkItemRecordV1> ConsumeWorkItemAsync(
            Guid workItemId, WorkConsumeRequestV1 body, CancellationToken ct)
        {
            Interlocked.Increment(ref _consumeAttempts);
            lock (_lock)
            {
                var item = _items.Values.FirstOrDefault(i => i.WorkItemId == workItemId);
                if (item is null
                    || item.Status != "completed"
                    || item.ArtifactRef is null
                    || _hashes.GetValueOrDefault(item.WorkItemId) != body.DownloadTokenHash
                    || item.Version != body.ExpectedVersion)
                {
                    throw new GenericCaseApiException(409, "work.lease_conflict");
                }

                var consumed = Copy(item, status: "consumed", version: item.Version + 1, leaseToken: null,
                    artifactRef: item.ArtifactRef, artifactExpiresAt: item.ArtifactExpiresAt);
                _items[item.SubjectRef] = consumed;
                _hashes[item.WorkItemId] = null;
                Interlocked.Increment(ref _consumeWins);
                return Task.FromResult(consumed);
            }
        }

        // W1-09 rails, unused by the W1-06 mirror.
        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> FailWorkItemAsync(
            Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();

        private static WorkItemRecordV1 Copy(
            WorkItemRecordV1 item,
            string status,
            int version,
            Guid? leaseToken,
            string? artifactRef = null,
            DateTimeOffset? artifactExpiresAt = null) => new()
        {
            WorkItemId = item.WorkItemId,
            Application = item.Application,
            Kind = item.Kind,
            SubjectRef = item.SubjectRef,
            Status = status,
            DueAt = item.DueAt,
            Version = version,
            LeaseToken = leaseToken,
            ArtifactRef = artifactRef,
            ArtifactExpiresAt = artifactExpiresAt,
        };
    }

    private sealed class ThrowingOwnershipClient : IStateOwnershipClient
    {
        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            Task.FromException<WorkItemRecordV1>(new GenericCaseApiException(503, "state-service is down"));

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct) =>
            Task.FromException<WorkItemRecordV1?>(new GenericCaseApiException(503, "state-service is down"));

        public Task<WorkItemRecordV1> CompleteWorkItemAsync(
            Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> ConsumeWorkItemAsync(
            Guid workItemId, WorkConsumeRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> FailWorkItemAsync(
            Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
