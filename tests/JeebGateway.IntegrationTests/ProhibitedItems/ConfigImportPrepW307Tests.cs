using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cms;
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

// gwdbx W3-07 PREP — the freeze-import runner + read-only parity check. The parity checker is
// the W3-11 flip bar: import -> clean, any local drift after import -> named mismatch.
public class ConfigImportPrepW307Tests
{
    // ----- parity after import ----------------------------------------------

    [Fact]
    public async Task Import_Then_Parity_Is_Clean()
    {
        var world = await World.SeededAsync();
        await world.Importer.ImportAsync(force: false, default);

        var report = await world.Checker.CheckAsync(default);

        report.Clean.Should().BeTrue(
            "an untouched import must verify clean; mismatches: {0}", string.Join(" | ", report.Mismatches));
        report.LexiconLocalActive.Should().Be(2);
        report.LexiconUpstreamActive.Should().Be(2);
        report.LexiconUpstreamTag.Should().Be(report.LexiconLocalTag);
        report.AcksChecked.Should().Be(2);
        report.AcksMatched.Should().Be(2);
        report.FlaggedRows.Should().Be(2);
        report.FlaggedSubjects.Should().Be(2);
        report.FlaggedMatched.Should().Be(2);
        // InMemoryCmsSurfaceStore seeds the five canonical surfaces, each with a published v1.
        report.CmsSurfacesChecked.Should().Be(5);
        report.CmsSurfacesMatched.Should().Be(5);
    }

    [Fact]
    public async Task Import_DoubleRun_Is_A_NoOp()
    {
        var world = await World.SeededAsync();
        await world.Importer.ImportAsync(force: false, default);
        var mintedAfterFirst = world.Config.TotalMintedVersions;

        // A14 — imports double-run to a no-op: idempotent publish keys mint nothing new.
        await world.Importer.ImportAsync(force: false, default);

        world.Config.TotalMintedVersions.Should().Be(mintedAfterFirst);
        (await world.Checker.CheckAsync(default)).Clean.Should().BeTrue();
    }

    // ----- parity refuses vacuous green -------------------------------------

    [Fact]
    public async Task Parity_Without_Import_Reports_Every_Leg()
    {
        var world = await World.SeededAsync();

        var report = await world.Checker.CheckAsync(default);

        report.Clean.Should().BeFalse("nothing was imported, so a clean report would be vacuous");
        report.Mismatches.Should().Contain(m => m.StartsWith("lexicon:"));
        report.Mismatches.Should().Contain(m => m.StartsWith("acks:"));
        report.Mismatches.Should().Contain(m => m.StartsWith("flagged:"));
        report.Mismatches.Should().Contain(m => m.StartsWith("cms:"));
    }

    [Fact]
    public async Task Parity_Catches_Local_Lexicon_Drift_After_Import()
    {
        var world = await World.SeededAsync();
        await world.Importer.ImportAsync(force: false, default);

        var page = await world.Lexicon.ListAllAsync(1, 10, default);
        var target = page.Items.First(i => i.Active);
        await world.Lexicon.UpdateAsync(
            target.Id, new ProhibitedItemPatch { Name = "renamed-after-import" }, "admin", default);

        var report = await world.Checker.CheckAsync(default);

        report.Clean.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.StartsWith("lexicon:"));
    }

    [Fact]
    public async Task Parity_Catches_An_Ack_Missing_Upstream()
    {
        var world = await World.SeededAsync();
        await world.Importer.ImportAsync(force: false, default);

        await world.Lexicon.AcknowledgeAsync("user-3-late", "some-version", default);

        var report = await world.Checker.CheckAsync(default);

        report.Clean.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Contains("user-3-late"));
    }

    [Fact]
    public async Task Parity_Catches_Cms_Version_Drift_After_Import()
    {
        var world = await World.SeededAsync();
        await world.Importer.ImportAsync(force: false, default);

        world.Cms.Publish("ofl-cms-orders-mfe", "admin", DateTimeOffset.UtcNow);

        var report = await world.Checker.CheckAsync(default);

        report.Clean.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.StartsWith("cms:") && m.Contains("version differs"));
    }

    [Fact]
    public async Task Parity_Catches_A_Flagged_Subject_Missing_Upstream()
    {
        var world = await World.SeededAsync();
        await world.Importer.ImportAsync(force: false, default);

        world.Flagged.AddRow("f-99", "user-new-subject");

        var report = await world.Checker.CheckAsync(default);

        report.Clean.Should().BeFalse();
        report.Mismatches.Should().Contain(m => m.Contains("user-new-subject"));
    }

    // ----- the worker ships inert -------------------------------------------

    [Fact]
    public async Task Worker_Disarmed_Touches_Nothing()
    {
        var world = await World.SeededAsync();
        var worker = world.NewWorker(new ConfigImportRunOptions { Enabled = false });

        await worker.StartAsync(default);
        await worker.ExecuteTask!;

        worker.LastImportReport.Should().BeNull();
        worker.LastParityReport.Should().BeNull();
        world.Config.TotalCalls.Should().Be(0, "a disarmed worker must make no upstream call");
        world.Ownership.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task Worker_Armed_DryRun_Is_Parity_Only_With_Zero_Writes()
    {
        var world = await World.SeededAsync();
        var worker = world.NewWorker(new ConfigImportRunOptions { Enabled = true, DryRun = true });

        await worker.StartAsync(default);
        await worker.ExecuteTask!;

        worker.LastImportReport.Should().BeNull("dry-run must not import");
        worker.LastParityReport.Should().NotBeNull();
        worker.LastParityReport!.Clean.Should().BeFalse("nothing was imported yet");
        world.Config.WriteCalls.Should().Be(0, "dry-run is read-only");
        world.Ownership.WriteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Worker_Armed_Execute_Imports_Then_Verifies_Clean()
    {
        var world = await World.SeededAsync();
        var worker = world.NewWorker(new ConfigImportRunOptions { Enabled = true, DryRun = false });

        await worker.StartAsync(default);
        await worker.ExecuteTask!;

        worker.LastImportReport.Should().NotBeNull();
        worker.LastImportReport!.LexiconItems.Should().Be(3);
        worker.LastParityReport.Should().NotBeNull();
        worker.LastParityReport!.Clean.Should().BeTrue(
            "mismatches: {0}", string.Join(" | ", worker.LastParityReport.Mismatches));
    }

    [Fact]
    public async Task Worker_Survives_An_Unwired_StateService()
    {
        var world = await World.SeededAsync();
        var worker = world.NewWorker(
            new ConfigImportRunOptions { Enabled = true, DryRun = true },
            config: new UnavailableStateConfigClient());

        // The prep run must fail soft: an unwired upstream aborts the run, never the host.
        await worker.StartAsync(default);
        var run = async () => await worker.ExecuteTask!;
        await run.Should().NotThrowAsync();
    }

    // ----- world -------------------------------------------------------------

    private sealed class World
    {
        public InMemoryProhibitedItemsStore Lexicon { get; } = new();
        public MutableFlaggedRequestStore Flagged { get; } = new();
        public InMemoryCmsSurfaceStore Cms { get; } = new();
        public StatefulConfigClient Config { get; } = new();
        public StatefulOwnershipClient Ownership { get; } = new();

        public StateServiceConfigImporter Importer => new(
            Lexicon, Flagged, Cms, Config, Ownership,
            new StaticOptionsMonitor<GwdbxMigrationOptions>(new GwdbxMigrationOptions()),
            NullLogger<StateServiceConfigImporter>.Instance);

        public ConfigParityChecker Checker => NewChecker(Config);

        public ConfigParityChecker NewChecker(IStateConfigClient config) => new(
            Lexicon, Flagged, Cms, config, Ownership, NullLogger<ConfigParityChecker>.Instance);

        public static async Task<World> SeededAsync()
        {
            var world = new World();
            await world.Lexicon.CreateAsync(
                new ProhibitedItemCreate { Name = "Fireworks", Category = "explosives" }, "admin", default);
            await world.Lexicon.CreateAsync(
                new ProhibitedItemCreate { Name = "Bleach", Category = "chemicals" }, "admin", default);
            var inactive = await world.Lexicon.CreateAsync(
                new ProhibitedItemCreate { Name = "Retired", Category = "misc" }, "admin", default);
            await world.Lexicon.UpdateAsync(
                inactive.Id, new ProhibitedItemPatch { Active = false }, "admin", default);

            var active = await world.Lexicon.ListActiveAsync(default);
            var version = ModerationGate.ComputeLexiconVersion(active);
            await world.Lexicon.AcknowledgeAsync("user-1", version, default);
            await world.Lexicon.AcknowledgeAsync("user-2", version, default);

            world.Flagged.AddRow("f-1", "user-1");
            world.Flagged.AddRow("f-2", "user-2");

            world.Cms.UpsertDraft("ofl-cms-orders-mfe", NewConfig("v1-value"));
            world.Cms.Publish("ofl-cms-orders-mfe", "admin", DateTimeOffset.UnixEpoch);
            world.Cms.UpsertDraft("ofl-cms-orders-mfe", NewConfig("draft-value"));
            return world;
        }

        public ConfigImportWorker NewWorker(ConfigImportRunOptions options, IStateConfigClient? config = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IProhibitedItemsStore>(Lexicon);
            services.AddSingleton<IFlaggedRequestStore>(Flagged);
            services.AddSingleton<ICmsSurfaceStore>(Cms);
            services.AddSingleton(config ?? (IStateConfigClient)Config);
            services.AddSingleton<IStateOwnershipClient>(Ownership);
            services.AddSingleton<IOptionsMonitor<GwdbxMigrationOptions>>(
                new StaticOptionsMonitor<GwdbxMigrationOptions>(new GwdbxMigrationOptions()));
            services.AddTransient(sp => new StateServiceConfigImporter(
                sp.GetRequiredService<IProhibitedItemsStore>(),
                sp.GetRequiredService<IFlaggedRequestStore>(),
                sp.GetRequiredService<ICmsSurfaceStore>(),
                sp.GetRequiredService<IStateConfigClient>(),
                sp.GetRequiredService<IStateOwnershipClient>(),
                sp.GetRequiredService<IOptionsMonitor<GwdbxMigrationOptions>>(),
                NullLogger<StateServiceConfigImporter>.Instance));
            services.AddTransient(sp => new ConfigParityChecker(
                sp.GetRequiredService<IProhibitedItemsStore>(),
                sp.GetRequiredService<IFlaggedRequestStore>(),
                sp.GetRequiredService<ICmsSurfaceStore>(),
                sp.GetRequiredService<IStateConfigClient>(),
                sp.GetRequiredService<IStateOwnershipClient>(),
                NullLogger<ConfigParityChecker>.Instance));

            return new ConfigImportWorker(
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<ConfigImportWorker>.Instance);
        }

        private static CmsConfig NewConfig(string value) =>
            new() { Data = new Dictionary<string, object?> { ["key"] = value } };
    }

    // ----- stateful fakes (draft->publish + idempotent keys, like the real primitive) --------

    private sealed class StatefulConfigClient : IStateConfigClient
    {
        private sealed class SurfaceState
        {
            public string? Title;
            public JsonElement? Draft;
            public List<ConfigVersionRecordV1> Versions { get; } = new();
        }

        private readonly Dictionary<string, SurfaceState> _surfaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigVersionRecordV1> _publishKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConfigAckRecordV1> _acks = new(StringComparer.Ordinal);

        public int WriteCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int TotalCalls => WriteCalls + ReadCalls;
        public int TotalMintedVersions => _surfaces.Values.Sum(s => s.Versions.Count);

        public Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
            string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct)
        {
            WriteCalls++;
            var state = GetOrAdd(surfaceKey);
            state.Title = body.Title;
            state.Draft = body.Data.Clone();
            return Task.FromResult(Record(surfaceKey, state));
        }

        public Task<ConfigVersionRecordV1> PublishAsync(
            string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            WriteCalls++;
            if (_publishKeys.TryGetValue(idempotencyKey, out var replay))
                return Task.FromResult(replay);

            var state = GetOrAdd(surfaceKey);
            var version = new ConfigVersionRecordV1
            {
                Version = state.Versions.Count + 1,
                Data = state.Draft ?? default,
                VersionTag = body.VersionTag ?? (state.Versions.Count + 1).ToString(),
                PublishedByRef = body.PublishedByRef,
                PublishedAt = body.PublishedAt ?? DateTimeOffset.UtcNow,
            };
            state.Versions.Add(version);
            _publishKeys[idempotencyKey] = version;
            return Task.FromResult(version);
        }

        public Task<ConfigSurfaceRecordV1?> GetSurfaceAsync(
            string application, string surfaceKey, CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(_surfaces.TryGetValue(surfaceKey, out var state)
                ? Record(surfaceKey, state)
                : (ConfigSurfaceRecordV1?)null);
        }

        public Task<ConfigAckRecordV1> UpsertAckAsync(
            string subjectRef, string surfaceKey, ConfigAckUpsertRequestV1 body, CancellationToken ct)
        {
            WriteCalls++;
            var record = new ConfigAckRecordV1
            {
                SubjectRef = subjectRef,
                SurfaceKey = surfaceKey,
                Version = body.Version,
                AckedAt = body.AckedAt ?? DateTimeOffset.UtcNow,
            };
            _acks[subjectRef + "|" + surfaceKey] = record;
            return Task.FromResult(record);
        }

        public Task<ConfigAckRecordV1?> GetAckAsync(
            string application, string subjectRef, string surfaceKey, CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(
                _acks.TryGetValue(subjectRef + "|" + surfaceKey, out var ack) ? ack : (ConfigAckRecordV1?)null);
        }

        private SurfaceState GetOrAdd(string surfaceKey)
        {
            if (!_surfaces.TryGetValue(surfaceKey, out var state))
                _surfaces[surfaceKey] = state = new SurfaceState();
            return state;
        }

        private static ConfigSurfaceRecordV1 Record(string surfaceKey, SurfaceState state) => new()
        {
            SurfaceKey = surfaceKey,
            Application = StateServiceConfigImporter.Application,
            Title = state.Title,
            Draft = state.Draft ?? default,
            LatestVersion = state.Versions.Count,
            Published = state.Versions.Count > 0 ? state.Versions[^1] : null,
        };
    }

    private sealed class StatefulOwnershipClient : IStateOwnershipClient
    {
        private readonly Dictionary<string, WorkItemRecordV1> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WorkItemRecordV1> _latest = new(StringComparer.Ordinal);

        public int WriteCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int TotalCalls => WriteCalls + ReadCalls;

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            WriteCalls++;
            if (_byKey.TryGetValue(idempotencyKey, out var replay))
                return Task.FromResult(replay);

            var record = new WorkItemRecordV1
            {
                WorkItemId = Guid.NewGuid(),
                Application = body.Application,
                Kind = body.Kind,
                SubjectRef = body.SubjectRef,
                Status = "queued",
                Payload = body.Payload ?? default,
            };
            _byKey[idempotencyKey] = record;
            _latest[body.Application + "|" + body.Kind + "|" + body.SubjectRef] = record;
            return Task.FromResult(record);
        }

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(_latest.TryGetValue(
                application + "|" + kind + "|" + subjectRef, out var item) ? item : (WorkItemRecordV1?)null);
        }

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
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

    private sealed class MutableFlaggedRequestStore : IFlaggedRequestStore
    {
        private readonly List<FlaggedRequest> _rows = new();

        public void AddRow(string id, string userId) => _rows.Add(new FlaggedRequest
        {
            Id = id,
            RequestId = "r-" + id,
            UserId = userId,
            Description = "flagged text",
            Matches = Array.Empty<ProhibitedItemMatch>(),
            Status = FlaggedRequestStatus.Pending,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

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

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
