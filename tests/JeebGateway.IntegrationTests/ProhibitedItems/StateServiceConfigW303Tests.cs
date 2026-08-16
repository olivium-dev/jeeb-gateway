using System.Text.Json;
using FluentAssertions;
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

// gwdbx W3-03 — the prohibited-items read seam on ONE state-service config primitive (G-27).
// The freeze-import cases left with the importer at ADR-0010; what remains is the ladder default,
// the "local" rung taking no state-service dependency, and the fail-open contract above it.
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
        // RE-SEALED 34 -> 30 at W2-R02 (G-08): the four settlement entries left the roster.
        StoreDurabilityGuard.Critical.Should().HaveCount(30,
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
}
