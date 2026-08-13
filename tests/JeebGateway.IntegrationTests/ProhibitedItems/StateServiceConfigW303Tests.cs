using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cms;
using JeebGateway.Infrastructure;
using JeebGateway.Migration;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Config;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.ProhibitedItems;

// gwdbx W3-03 — the prohibited-items trio + cms-config pair on ONE state-service config primitive
// (G-27). Charter cases: import path, both modes default to "local", and an upstream failure never
// fails the user-facing lexicon read at the "local" rung.
public class StateServiceConfigW303Tests
{
    // ----- ladder defaults ----------------------------------------------------

    [Fact]
    public void Both_W3_03_Modes_Default_To_Local()
    {
        var options = new GwdbxMigrationOptions();

        options.ProhibitedItemsMode.Should().Be("local",
            "A10 — W3-03 lands the rail dormant; the flip is W3-11");
        options.CmsConfigMode.Should().Be("local");
        options.ProhibitedItems.Should().Be(GwdbxMigrationPhase.Local);
        options.CmsConfig.Should().Be(GwdbxMigrationPhase.Local);
    }

    // ----- local rung: no new dependency on the live create path ---------------

    [Fact]
    public async Task Local_Rung_Serves_The_Lexicon_With_A_Dead_State_Service_And_Never_Calls_It()
    {
        var upstream = new ThrowingConfigClient();
        var store = NewStore(upstream, "local", out var inner);
        await Seed(inner, ("knife", "weapons", true));

        var items = await store.ListActiveAsync(default);

        items.Should().ContainSingle().Which.Name.Should().Be("knife");
        upstream.Attempts.Should().Be(0,
            "at 'local' the create-time moderation gate takes NO dependency on state-service");
    }

    [Fact]
    public async Task Local_Rung_Failure_Probe_Really_Can_Fail()
    {
        var upstream = new ThrowingConfigClient();

        var act = async () => await upstream.GetSurfaceAsync("jeeb-gateway", "moderation-lexicon", default);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the double used above genuinely throws — the local-rung probe is not vacuous");
    }

    // ----- read flip: upstream serves, and fails OPEN --------------------------

    [Fact]
    public async Task Read_Flip_Serves_The_Published_Surface_Active_Items_Only()
    {
        var upstream = new RecordingConfigClient();
        var store = NewStore(upstream, "dual-write-upstream-read", out var inner);
        await Seed(inner, ("local-only", "weapons", true));
        upstream.Published = ProhibitedItemsEnvelope.Serialize(new[]
        {
            Item("upstream-b", "weapons", active: true),
            Item("upstream-a", "weapons", active: true),
            Item("retired", "weapons", active: false),
        });

        var items = await store.ListActiveAsync(default);

        upstream.SurfaceReads.Should().Be(1, "the flipped read really reached the config surface");
        items.Select(i => i.Name).Should().Equal("upstream-a", "upstream-b");
    }

    [Fact]
    public async Task Read_Flip_Fails_Open_To_The_Local_Lexicon_When_State_Service_Is_Down()
    {
        var upstream = new ThrowingConfigClient();
        var store = NewStore(upstream, "dual-write-upstream-read", out var inner);
        await Seed(inner, ("knife", "weapons", true));

        var items = await store.ListActiveAsync(default);

        upstream.Attempts.Should().Be(1, "the probe really reached the throwing upstream");
        items.Should().ContainSingle().Which.Name.Should().Be("knife",
            "a state-service blip must never block request creation (an empty lexicon is a 503)");
    }

    [Fact]
    public async Task Read_Flip_Fails_Open_When_The_Surface_Published_Nothing()
    {
        var upstream = new RecordingConfigClient { Published = null };
        var store = NewStore(upstream, "dual-write-upstream-read", out var inner);
        await Seed(inner, ("knife", "weapons", true));

        var items = await store.ListActiveAsync(default);

        items.Should().ContainSingle().Which.Name.Should().Be("knife",
            "a missed import is indistinguishable from an empty surface, so it fails open too");
    }

    [Fact]
    public async Task Round_Trip_Preserves_The_Lexicon_Version_So_Recorded_Acks_Still_Clear()
    {
        var upstream = new RecordingConfigClient();
        var store = NewStore(upstream, "dual-write-upstream-read", out var inner);
        await Seed(inner, ("knife", "weapons", true), ("bleach", "chemicals", true));
        var local = await inner.ListActiveAsync(default);
        upstream.Published = ProhibitedItemsEnvelope.Serialize(local);

        var served = await store.ListActiveAsync(default);

        ModerationGate.ComputeLexiconVersion(served)
            .Should().Be(ModerationGate.ComputeLexiconVersion(local),
                "the ack version token is derived from UpdatedAt — a lossy round trip un-acks everyone");
    }

    // ----- import path --------------------------------------------------------

    [Fact]
    public async Task Import_Publishes_The_Whole_Catalog_Under_The_Gateway_Lexicon_Version()
    {
        var config = new RecordingConfigClient();
        var lexicon = new InMemoryProhibitedItemsStore();
        await Seed(lexicon, ("knife", "weapons", true), ("retired", "weapons", false));
        var importer = NewImporter(config, lexicon: lexicon);

        var report = await importer.ImportAsync(force: false, default);

        var active = await lexicon.ListActiveAsync(default);
        var expected = ModerationGate.ComputeLexiconVersion(active);
        report.LexiconItems.Should().Be(2, "inactive rows travel too — the import is a full replay");
        report.LexiconVersionTag.Should().Be(expected);
        config.Drafts.Should().Contain(d => d.SurfaceKey == "moderation-lexicon");
        var publish = config.Publishes.Single(p => p.SurfaceKey == "moderation-lexicon");
        publish.IdempotencyKey.Should().Be("config-import:moderation-lexicon:" + expected,
            "G-15 — replaying one lexicon version must never mint a second upstream version");
        publish.Body.VersionTag.Should().Be(expected);
    }

    [Fact]
    public async Task Import_Replays_The_Ack_Ledger_Onto_The_Same_Surface()
    {
        var config = new RecordingConfigClient();
        var lexicon = new InMemoryProhibitedItemsStore();
        await Seed(lexicon, ("knife", "weapons", true));
        await lexicon.AcknowledgeAsync("u-1", "v-old", default);
        await lexicon.AcknowledgeAsync("u-2", "v-new", default);
        var importer = NewImporter(config, lexicon: lexicon);

        var report = await importer.ImportAsync(force: false, default);

        report.Acks.Should().Be(2);
        config.Acks.Select(a => (a.SubjectRef, a.Body.Version))
            .Should().BeEquivalentTo(new[] { ("u-1", "v-old"), ("u-2", "v-new") });
        config.Acks.Should().OnlyContain(a => a.SurfaceKey == "moderation-lexicon",
            "acks key to a surface version — that is why one primitive covers both legs");
    }

    [Fact]
    public async Task Import_Moves_Flagged_Requests_Onto_Work_Items_With_A_Stable_Key()
    {
        var config = new RecordingConfigClient();
        var ownership = new RecordingOwnershipClient();
        var flagged = new FakeFlaggedRequestStore(NewFlag("f-1", "u-1"), NewFlag("f-2", "u-2"));
        var importer = NewImporter(config, ownership: ownership, flagged: flagged);

        var report = await importer.ImportAsync(force: false, default);

        report.FlaggedRequests.Should().Be(2);
        ownership.Creates.Select(c => c.IdempotencyKey)
            .Should().Equal("content-flag:f-1", "content-flag:f-2");
        ownership.Creates[0].Body.Kind.Should().Be("content-flag",
            "G-28 — the moderation queue rides the existing work-item rail with neutral vocabulary");
        ownership.Creates[0].Body.SubjectRef.Should().Be("u-1");
    }

    [Fact]
    public async Task Import_Replays_The_Cms_Version_History_In_Order_Then_The_Draft()
    {
        var config = new RecordingConfigClient();
        var cms = new FakeCmsSurfaceStore();
        var importer = NewImporter(config, cms: cms);

        var report = await importer.ImportAsync(force: false, default);

        report.CmsVersions.Should().Be(2);
        report.CmsDrafts.Should().Be(1);
        config.Publishes.Where(p => p.SurfaceKey == "ofl-cms-orders-mfe")
            .Select(p => p.IdempotencyKey)
            .Should().Equal("config-import:ofl-cms-orders-mfe:v1", "config-import:ofl-cms-orders-mfe:v2");
        config.Drafts.Last(d => d.SurfaceKey == "ofl-cms-orders-mfe").Body.Data.GetRawText()
            .Should().Contain("draft-value", "the working copy lands last so the replay cannot bury it");
    }

    [Fact]
    public async Task Import_Covers_Every_Seeded_Cms_Surface()
    {
        var config = new RecordingConfigClient();
        var cms = new InMemoryCmsSurfaceStore();
        var importer = NewImporter(config, cms: cms);

        var report = await importer.ImportAsync(force: false, default);

        report.CmsVersions.Should().Be(cms.ListSurfaces().Sum(s => s.Versions.Count));
        report.CmsVersions.Should().BeGreaterThan(0, "the real seed publishes a v1 per surface");
    }

    [Fact]
    public async Task Import_Is_Idempotent_By_Key_Across_Re_Runs()
    {
        var config = new RecordingConfigClient();
        var lexicon = new InMemoryProhibitedItemsStore();
        await Seed(lexicon, ("knife", "weapons", true));
        var importer = NewImporter(config, lexicon: lexicon);

        await importer.ImportAsync(force: false, default);
        var first = config.Publishes.Select(p => p.IdempotencyKey).ToList();
        await importer.ImportAsync(force: false, default);

        config.Publishes.Select(p => p.IdempotencyKey).Skip(first.Count).Should().Equal(first,
            "G-21 — a re-run replays the same keys, so upstream no-ops instead of duplicating");
    }

    [Fact]
    public async Task Import_Refuses_A_Leg_That_Is_Already_Serving_Upstream_Reads_Unless_Forced()
    {
        var config = new RecordingConfigClient();
        var lexicon = new InMemoryProhibitedItemsStore();
        await Seed(lexicon, ("knife", "weapons", true));
        var importer = NewImporter(config, lexicon: lexicon, prohibitedMode: "upstream-authority");

        var guarded = await importer.ImportAsync(force: false, default);
        config.Publishes.Should().BeEmpty("re-publishing would swap the LIVE lexicon under the gate");
        guarded.SkippedLegs.Should().ContainSingle().Which.Should().StartWith("prohibited-items:");

        var forced = await importer.ImportAsync(force: true, default);
        forced.SkippedLegs.Should().BeEmpty();
        config.Publishes.Should().ContainSingle(p => p.SurfaceKey == "moderation-lexicon");
    }

    // ----- guard roster (G-08) ------------------------------------------------

    [Fact]
    public void Read_Seam_Is_An_Approved_Durable_Implementation_Of_IProhibitedItemsStore()
    {
        var entry = StoreDurabilityGuard.Critical
            .Single(c => c.Iface == typeof(IProhibitedItemsStore));

        entry.DurableImpls.Should().BeEquivalentTo(new[]
        {
            typeof(PostgresProhibitedItemsStore),
            typeof(StateServiceProhibitedItemsStore)
        }, "G-08 — the seam wraps the durable inner catalog, so both resolutions pass the boot gate");
        StoreDurabilityGuard.Critical.Should().HaveCount(34,
            "W3-03 adds an implementation to an existing entry; the roster COUNT is unchanged");
    }

    // ----- helpers -----------------------------------------------------------

    private static StateServiceProhibitedItemsStore NewStore(
        IStateConfigClient upstream, string mode, out InMemoryProhibitedItemsStore inner)
    {
        inner = new InMemoryProhibitedItemsStore();
        var services = new ServiceCollection();
        services.AddSingleton(upstream);

        return new StateServiceProhibitedItemsStore(
            inner,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<GwdbxMigrationOptions>(
                new GwdbxMigrationOptions { ProhibitedItemsMode = mode }),
            NullLogger<StateServiceProhibitedItemsStore>.Instance);
    }

    private static StateServiceConfigImporter NewImporter(
        IStateConfigClient config,
        IProhibitedItemsStore? lexicon = null,
        IFlaggedRequestStore? flagged = null,
        ICmsSurfaceStore? cms = null,
        IStateOwnershipClient? ownership = null,
        string prohibitedMode = "local",
        string cmsMode = "local") =>
        new(
            lexicon ?? new InMemoryProhibitedItemsStore(),
            flagged ?? new FakeFlaggedRequestStore(),
            cms ?? new FakeCmsSurfaceStore(empty: true),
            config,
            ownership ?? new RecordingOwnershipClient(),
            new StaticOptionsMonitor<GwdbxMigrationOptions>(new GwdbxMigrationOptions
            {
                ProhibitedItemsMode = prohibitedMode,
                CmsConfigMode = cmsMode,
            }),
            NullLogger<StateServiceConfigImporter>.Instance);

    private static async Task Seed(
        IProhibitedItemsStore store, params (string Name, string Category, bool Active)[] items)
    {
        foreach (var (name, category, active) in items)
        {
            var created = await store.CreateAsync(
                new ProhibitedItemCreate { Name = name, Category = category }, "admin", default);
            if (!active)
            {
                await store.UpdateAsync(created.Id, new ProhibitedItemPatch { Active = false }, "admin", default);
            }
        }
    }

    private static ProhibitedItem Item(string name, string category, bool active) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = name,
        Category = category,
        Active = active,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static FlaggedRequest NewFlag(string id, string userId) => new()
    {
        Id = id,
        RequestId = "r-" + id,
        UserId = userId,
        Description = "flagged text",
        Matches = Array.Empty<ProhibitedItemMatch>(),
        Status = FlaggedRequestStatus.Pending,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingConfigClient : IStateConfigClient
    {
        public List<(string SurfaceKey, ConfigDraftUpsertRequestV1 Body)> Drafts { get; } = new();

        public List<(string SurfaceKey, string IdempotencyKey, ConfigPublishRequestV1 Body)> Publishes { get; } = new();

        public List<(string SubjectRef, string SurfaceKey, ConfigAckUpsertRequestV1 Body)> Acks { get; } = new();

        public int SurfaceReads { get; private set; }

        public JsonElement? Published { get; set; }

        public Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
            string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct)
        {
            Drafts.Add((surfaceKey, body));
            return Task.FromResult(new ConfigSurfaceRecordV1 { SurfaceKey = surfaceKey });
        }

        public Task<ConfigVersionRecordV1> PublishAsync(
            string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            Publishes.Add((surfaceKey, idempotencyKey, body));
            return Task.FromResult(new ConfigVersionRecordV1 { Version = Publishes.Count });
        }

        public Task<ConfigSurfaceRecordV1?> GetSurfaceAsync(
            string application, string surfaceKey, CancellationToken ct)
        {
            SurfaceReads++;
            return Task.FromResult<ConfigSurfaceRecordV1?>(new ConfigSurfaceRecordV1
            {
                SurfaceKey = surfaceKey,
                Application = application,
                Published = Published is { } data
                    ? new ConfigVersionRecordV1 { Version = 1, Data = data }
                    : null,
            });
        }

        public Task<ConfigAckRecordV1> UpsertAckAsync(
            string subjectRef, string surfaceKey, ConfigAckUpsertRequestV1 body, CancellationToken ct)
        {
            Acks.Add((subjectRef, surfaceKey, body));
            return Task.FromResult(new ConfigAckRecordV1 { SubjectRef = subjectRef });
        }

        public Task<ConfigAckRecordV1?> GetAckAsync(
            string application, string subjectRef, string surfaceKey, CancellationToken ct) =>
            Task.FromResult<ConfigAckRecordV1?>(null);
    }

    private sealed class ThrowingConfigClient : IStateConfigClient
    {
        public int Attempts { get; private set; }

        private Task<T> Fail<T>()
        {
            Attempts++;
            return Task.FromException<T>(new InvalidOperationException("state-service is down"));
        }

        public Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
            string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct) =>
            Fail<ConfigSurfaceRecordV1>();

        public Task<ConfigVersionRecordV1> PublishAsync(
            string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            Fail<ConfigVersionRecordV1>();

        public Task<ConfigSurfaceRecordV1?> GetSurfaceAsync(
            string application, string surfaceKey, CancellationToken ct) =>
            Fail<ConfigSurfaceRecordV1?>();

        public Task<ConfigAckRecordV1> UpsertAckAsync(
            string subjectRef, string surfaceKey, ConfigAckUpsertRequestV1 body, CancellationToken ct) =>
            Fail<ConfigAckRecordV1>();

        public Task<ConfigAckRecordV1?> GetAckAsync(
            string application, string subjectRef, string surfaceKey, CancellationToken ct) =>
            Fail<ConfigAckRecordV1?>();
    }

    private sealed class RecordingOwnershipClient : IStateOwnershipClient
    {
        public List<(string IdempotencyKey, WorkItemCreateRequestV1 Body)> Creates { get; } = new();

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            Creates.Add((idempotencyKey, body));
            return Task.FromResult(new WorkItemRecordV1 { WorkItemId = Guid.NewGuid() });
        }

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkItemRecordV1> CompleteWorkItemAsync(
            Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> FailWorkItemAsync(
            Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> ConsumeWorkItemAsync(
            Guid workItemId, WorkConsumeRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFlaggedRequestStore : IFlaggedRequestStore
    {
        private readonly List<FlaggedRequest> _rows;

        public FakeFlaggedRequestStore(params FlaggedRequest[] rows) => _rows = rows.ToList();

        public Task<FlaggedRequestPage> ListAsync(
            FlaggedRequestStatus? status, int page, int pageSize, CancellationToken ct) =>
            Task.FromResult(new FlaggedRequestPage
            {
                Items = _rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Total = _rows.Count,
            });

        public Task<FlaggedRequest> CreateAsync(FlaggedRequestCreate input, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<FlaggedRequest?> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<FlaggedRequest?> DecideAsync(
            string id, FlaggedRequestStatus status, string adminUserId, string? note, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCmsSurfaceStore : ICmsSurfaceStore
    {
        private readonly List<CmsSurface> _surfaces = new();

        public FakeCmsSurfaceStore(bool empty = false)
        {
            if (empty) return;

            var surface = new CmsSurface { SurfaceId = "ofl-cms-orders-mfe", Title = "Orders MFE" };
            surface.Versions.Add(NewVersion(1, "v1-value"));
            surface.Versions.Add(NewVersion(2, "v2-value"));
            surface.Draft = NewConfig("draft-value");
            _surfaces.Add(surface);
        }

        public IReadOnlyList<CmsSurface> ListSurfaces() => _surfaces;

        public CmsSurface? GetSurface(string surfaceId) =>
            _surfaces.FirstOrDefault(s => s.SurfaceId == surfaceId);

        public CmsSurface? UpsertDraft(string surfaceId, CmsConfig draft) => throw new NotSupportedException();

        public CmsConfigVersion? Publish(string surfaceId, string publishedByUserId, DateTimeOffset publishedAt) =>
            throw new NotSupportedException();

        private static CmsConfigVersion NewVersion(int version, string value) => new()
        {
            Version = version,
            Config = NewConfig(value),
            PublishedAt = DateTimeOffset.UnixEpoch,
            PublishedByUserId = "admin",
        };

        private static CmsConfig NewConfig(string value) =>
            new() { Data = new Dictionary<string, object?> { ["key"] = value } };
    }
}
