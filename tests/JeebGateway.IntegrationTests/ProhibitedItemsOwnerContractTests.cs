using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Infrastructure;
using JeebGateway.Migration;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

// Owner-contract tests for the prohibited-items catalog + ack ledger. Rewritten off the
// never-shipped BanServiceProhibitedItemsStore onto the live owner, StateServiceProhibitedItemsStore.
public sealed class ProhibitedItemsOwnerContractTests
{
    private const string ItemId = "6f1515fb-ace5-4868-bb80-0c4802c9300e";
    private const string ArchivedId = "2f1515fb-ace5-4868-bb80-0c4802c9300e";

    // ----- runtime composition ------------------------------------------------

    [Fact]
    public void Runtime_DI_Resolves_The_StateService_Owner_Adapter_Over_The_Local_Root()
    {
        using var factory = Host();

        var serving = factory.Services.GetRequiredService<IProhibitedItemsStore>();
        var local = factory.Services.GetRequiredService<ILocalProhibitedItemsStore>();

        serving.Should().BeOfType<StateServiceProhibitedItemsStore>(
            "one adapter serves the catalog; the gateway-local root is reachable only PAST it");
        local.Should().BeOfType<InMemoryProhibitedItemsStore>();
        ((StateServiceProhibitedItemsStore)serving).Inner.Should().BeSameAs(local,
            "the decorator must wrap the same local root a local-vs-upstream tool resolves, "
            + "otherwise the tool inspects a store nothing serves");
    }

    [Fact]
    public void Scanner_And_Its_Store_Share_One_Lifetime_So_Neither_Is_Captive()
    {
        using var factory = Host();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<IProhibitedItemScanner>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IProhibitedItemScanner>());
        first.ServiceProvider.GetRequiredService<IProhibitedItemsStore>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IProhibitedItemsStore>(),
                "the scanner captures the store for the host's lifetime, so a shorter-lived store "
                + "would be a captive dependency the container silently freezes");
    }

    // ----- version-pinned catalog snapshot ------------------------------------

    [Fact]
    public async Task ActiveCatalog_Pins_One_Published_Snapshot_And_Preserves_Description_And_Severity()
    {
        var upstream = new RecordingConfigClient
        {
            Published = ProhibitedItemsEnvelope.Serialize(new[]
            {
                Item(ItemId, "kitchen knife", "weapon", ProhibitedSeverity.Warn,
                    active: true, description: "Sharp kitchen implement", updated: "2026-08-02T00:00:00Z"),
                Item(ArchivedId, "archived", "old", ProhibitedSeverity.Block,
                    active: false, description: null, updated: "2026-08-03T00:00:00Z"),
            }),
        };
        var store = NewStore(upstream, "upstream-authority", out _);

        var catalog = await store.GetActiveCatalogAsync(default);

        catalog.Items.Should().ContainSingle();
        catalog.Items[0].Name.Should().Be("kitchen knife");
        catalog.Items[0].Description.Should().Be("Sharp kitchen implement",
            "a lossy config-surface round trip silently empties the description the admin UI shows");
        catalog.Items[0].Severity.Should().Be(ProhibitedSeverity.Warn);
        catalog.Version.Should().Be(ModerationGate.ComputeLexiconVersion(catalog.Items),
            "the pin must describe the snapshot actually served, or every recorded ack un-acks");
        catalog.Version.Should().NotContain("2026-08-03",
            "the pin is taken from the ACTIVE snapshot, never from an archived row outside it");
        upstream.SurfaceReads.Should().Be(1,
            "the snapshot is pinned by ONE owner read; a second read can serve a different catalog");
    }

    // ----- acknowledgement ledger ---------------------------------------------

    [Fact]
    public async Task Ack_Read_Is_Exact_Version_And_Returns_Null_For_Any_Other_Version()
    {
        var upstream = new RecordingConfigClient
        {
            Ack = new ConfigAckRecordV1
            {
                SubjectRef = "raw-user-id",
                SurfaceKey = ProhibitedItemsEnvelope.SurfaceKey,
                Version = "lexicon-v8",
                AckedAt = DateTimeOffset.Parse("2026-08-10T10:11:12Z"),
            },
        };
        var store = NewStore(upstream, "upstream-authority", out _);

        var other = await store.GetAcknowledgmentAsync("raw-user-id", "lexicon-v9", default);
        var exact = await store.GetAcknowledgmentAsync("raw-user-id", "lexicon-v8", default);

        other.Should().BeNull("an ack for a different catalog version must never satisfy the gate");
        exact.Should().NotBeNull();
        exact!.UserId.Should().Be("raw-user-id");
        exact.Version.Should().Be("lexicon-v8");
        exact.AcknowledgedAt.Should().Be(DateTimeOffset.Parse("2026-08-10T10:11:12Z"));
    }

    [Fact]
    public async Task Ack_Read_Returns_Null_When_The_Owner_Holds_No_Acknowledgement()
    {
        var upstream = new RecordingConfigClient { Ack = null };
        var store = NewStore(upstream, "upstream-authority", out _);

        var result = await store.GetAcknowledgmentAsync("raw-user-id", default);

        result.Should().BeNull();
        var read = upstream.AckReads.Should().ContainSingle().Which;
        read.Application.Should().Be(StateServiceProhibitedItemsStore.Application);
        read.SubjectRef.Should().Be("raw-user-id", "the owner subject is the RAW gateway user id");
        read.SurfaceKey.Should().Be(ProhibitedItemsEnvelope.SurfaceKey);
    }

    [Fact]
    public async Task Ack_Write_Sends_The_Raw_User_Subject_And_The_Jeeb_Application_Upstream()
    {
        const string userId = "user/42";
        const string version = "opaque/v 7";
        var upstream = new RecordingConfigClient();
        var store = NewStore(upstream, "upstream-authority", out _);

        var written = await store.AcknowledgeAsync(userId, version, default);

        written.UserId.Should().Be(userId);
        written.Version.Should().Be(version, "the acknowledged tag is echoed back verbatim");
        var (subject, surface, body) = upstream.AckWrites.Should().ContainSingle().Which;
        subject.Should().Be(userId, "a mangled subject writes the ack against the wrong user");
        surface.Should().Be(ProhibitedItemsEnvelope.SurfaceKey);
        body.Application.Should().Be(StateServiceProhibitedItemsStore.Application);
        body.Version.Should().Be(version);
    }

    // ----- catalog authoring ---------------------------------------------------

    [Fact]
    public async Task Catalog_Authoring_Fails_Closed_From_The_Read_Rung_Up()
    {
        var store = NewStore(new RecordingConfigClient(), "upstream-authority", out _);

        var create = async () => await store.CreateAsync(
            new ProhibitedItemCreate { Name = "knife", Category = "weapon" }, "admin-actor", default);
        var update = async () => await store.UpdateAsync(
            ItemId, new ProhibitedItemPatch { Active = false }, "admin-actor", default);

        (await create.Should().ThrowAsync<OwnerCapabilityUnavailableException>(
                "a local row the upstream-owned gate can never read is worse than a visible failure"))
            .Which.Capability.Should().Contain("jeeb-state-service");
        await update.Should().ThrowAsync<OwnerCapabilityUnavailableException>();
    }

    [Fact]
    public async Task Local_Rung_Create_And_Update_Map_Name_Description_Severity_And_Actor()
    {
        var store = NewStore(new RecordingConfigClient(), "local", out _);

        var created = await store.CreateAsync(new ProhibitedItemCreate
        {
            Name = " knife ",
            Category = "weapon",
            Description = "owner-preserved",
            Severity = ProhibitedSeverity.Warn,
        }, "admin-actor", default);
        var updated = await store.UpdateAsync(created.Id, new ProhibitedItemPatch
        {
            Description = "still-preserved",
            Active = false,
        }, "second-actor", default);

        created.Name.Should().Be("knife");
        created.Description.Should().Be("owner-preserved");
        created.Severity.Should().Be(ProhibitedSeverity.Warn);
        created.CreatedBy.Should().Be("admin-actor");
        updated.Should().NotBeNull();
        updated!.Description.Should().Be("still-preserved");
        updated.Name.Should().Be("knife", "a patch that omits the name must not clear it");
        updated.Severity.Should().Be(ProhibitedSeverity.Warn, "an omitted severity stays unchanged");
        updated.UpdatedBy.Should().Be("second-actor");
        updated.Active.Should().BeFalse();
    }

    [Fact]
    public async Task Duplicate_Name_Surfaces_As_A_Duplicate_Not_A_Generic_Catalog_Conflict()
    {
        var store = NewStore(new RecordingConfigClient(), "local", out _);
        await store.CreateAsync(
            new ProhibitedItemCreate { Name = "knife", Category = "weapon" }, "admin", default);

        var act = async () => await store.CreateAsync(
            new ProhibitedItemCreate { Name = " KNIFE ", Category = "weapon" }, "admin", default);

        await act.Should().ThrowAsync<DuplicateProhibitedItemNameException>().WithMessage("*KNIFE*");
        typeof(DuplicateProhibitedItemNameException).Should()
            .NotBeDerivedFrom<ProhibitedCatalogConflictException>(
                "a uniqueness violation and a concurrent-change conflict are different admin outcomes; "
                + "collapsing them tells an admin to retry a name that will never be free");
    }

    // ----- acknowledge endpoint 409 fidelity -----------------------------------

    [Fact]
    public async Task Acknowledge_Endpoint_Returns_409_When_The_Catalog_Changes_During_The_Atomic_Write()
    {
        var controller = Controller(new ThrowingAcknowledgementStore(
            "opaque-current",
            version => new StaleProhibitedCatalogVersionException(version, "catalog changed during write")));

        var result = await controller.Acknowledge(
            new ProhibitedItemsAcknowledgeRequest { Version = "opaque-current" }, default);

        var problem = result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Status.Should().Be(StatusCodes.Status409Conflict);
        problem.Detail.Should().Contain("opaque-current",
            "the pre-read is not the concurrency guard; the atomic write's 409 must reach the client");
    }

    [Fact]
    public async Task Generic_Catalog_Conflict_Is_Not_Reported_As_A_Stale_Version()
    {
        var controller = Controller(new ThrowingAcknowledgementStore(
            "opaque-current",
            _ => new ProhibitedCatalogConflictException("catalog changed concurrently")));

        var result = await controller.Acknowledge(
            new ProhibitedItemsAcknowledgeRequest { Version = "opaque-current" }, default);

        var problem = result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>().Which;
        problem.Status.Should().Be(StatusCodes.Status409Conflict);
        problem.Title.Should().Contain("while it was being acknowledged");
        problem.Detail.Should().BeNull(
            "a generic owner conflict must not be dressed up as a stale-tag rejection");
    }

    // ----- moderation gate ------------------------------------------------------

    [Fact]
    public async Task ModerationGate_Scans_The_Same_VersionPinned_Snapshot()
    {
        var item = Item(ItemId, "knife", "weapon", ProhibitedSeverity.Warn,
            active: true, description: null, updated: "2026-08-02T00:00:00Z");
        var store = new PinnedCatalogStore(new ProhibitedCatalogSnapshot([item], "pinned-owner-version"));
        var scanner = new ProhibitedItemScanner(store, new InMemorySynonymRegistry());

        var outcome = await new ModerationGate(store, scanner).EvaluateAsync("a knife", default);

        outcome.Version.Should().Be("pinned-owner-version");
        outcome.Scan.Matches.Should().ContainSingle(match => match.ItemId == ItemId);
    }

    // ----- helpers ---------------------------------------------------------------

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host() =>
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
            });

    private static IProhibitedItemsStore NewStore(
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

    private static ProhibitedItemsController Controller(IProhibitedItemsStore store) => new(store)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "raw-user")], "test")),
            },
        },
    };

    private static ProhibitedItem Item(
        string id,
        string name,
        string category,
        ProhibitedSeverity severity,
        bool active,
        string? description,
        string updated) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Description = description,
        Severity = severity,
        Active = active,
        CreatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse(updated),
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
        public List<(string Application, string SubjectRef, string SurfaceKey)> AckReads { get; } = new();

        public List<(string SubjectRef, string SurfaceKey, ConfigAckUpsertRequestV1 Body)> AckWrites { get; } = new();

        public int SurfaceReads { get; private set; }

        public JsonElement? Published { get; set; }

        public ConfigAckRecordV1? Ack { get; set; }

        public Task<ConfigSurfaceRecordV1> UpsertDraftAsync(
            string surfaceKey, ConfigDraftUpsertRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException("nothing under test authors the published surface");

        public Task<ConfigVersionRecordV1> PublishAsync(
            string surfaceKey, ConfigPublishRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException("nothing under test publishes the surface");

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
            AckWrites.Add((subjectRef, surfaceKey, body));
            return Task.FromResult(new ConfigAckRecordV1
            {
                SubjectRef = subjectRef,
                SurfaceKey = surfaceKey,
                Version = body.Version,
                AckedAt = body.AckedAt ?? DateTimeOffset.UtcNow,
            });
        }

        public Task<ConfigAckRecordV1?> GetAckAsync(
            string application, string subjectRef, string surfaceKey, CancellationToken ct)
        {
            AckReads.Add((application, subjectRef, surfaceKey));
            return Task.FromResult(Ack);
        }
    }

    private sealed class PinnedCatalogStore(ProhibitedCatalogSnapshot snapshot) : IProhibitedItemsStore
    {
        public Task<ProhibitedCatalogSnapshot> GetActiveCatalogAsync(CancellationToken ct) =>
            Task.FromResult(snapshot);

        public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct) =>
            throw new InvalidOperationException("the pinned scan must not perform a second owner read");

        public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem> CreateAsync(
            ProhibitedItemCreate input, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem?> UpdateAsync(
            string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<UserAcknowledgment> AcknowledgeAsync(
            string userId, string version, CancellationToken ct) => throw new NotSupportedException();

        public Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(
            int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ThrowingAcknowledgementStore(string version, Func<string, Exception> onWrite)
        : IProhibitedItemsStore
    {
        public Task<ProhibitedCatalogSnapshot> GetActiveCatalogAsync(CancellationToken ct) =>
            Task.FromResult(new ProhibitedCatalogSnapshot([], version));

        public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem> CreateAsync(
            ProhibitedItemCreate input, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProhibitedItem?> UpdateAsync(
            string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<UserAcknowledgment> AcknowledgeAsync(
            string userId, string acknowledgedVersion, CancellationToken ct) =>
            throw onWrite(acknowledgedVersion);

        public Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(
            int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
    }
}
