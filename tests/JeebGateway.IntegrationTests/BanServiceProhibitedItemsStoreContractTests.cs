using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class BanServiceProhibitedItemsStoreContractTests
{
    private const string ItemId = "6f1515fb-ace5-4868-bb80-0c4802c9300e";

    [Fact]
    public void Runtime_DI_Resolves_Only_The_BanService_Owner_Adapter()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
            });

        factory.Services.GetRequiredService<IProhibitedItemsStore>()
            .Should().BeOfType<BanServiceProhibitedItemsStore>();
    }

    [Fact]
    public void Moderation_Scanner_Is_Scoped_With_The_Transient_Owner_Adapter()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("DELIVERY_SERVICE_TOKEN", new string('t', 48));
            });
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();

        firstScope.ServiceProvider.GetRequiredService<IProhibitedItemScanner>()
            .Should().NotBeSameAs(
                secondScope.ServiceProvider.GetRequiredService<IProhibitedItemScanner>());
    }

    [Fact]
    public async Task ActiveCatalog_Pins_Immutable_Opaque_Version_And_Preserves_Description()
    {
        const string version = "legacy/2026-08-09T23:59:58.1234567+00:00";
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK,
                    $$"""{"catalog_key":"jeeb-prohibited-items","current_revision":91,"current_version_tag":"{{version}}","versions":[]}""")
                : Json(HttpStatusCode.OK,
                    $$"""{"catalog_key":"jeeb-prohibited-items","revision":91,"version_tag":"{{version}}","created_at":"2026-08-09T23:59:58Z","items":[{"id":"{{ItemId}}","list_key":"jeeb-prohibited-items","category":"weapon","keyword":"kitchen knife","description":"Sharp kitchen implement","severity":"warn","language":"en","active":true,"created_at":"2026-08-01T00:00:00Z","updated_at":"2026-08-02T00:00:00Z"},{"id":"2f1515fb-ace5-4868-bb80-0c4802c9300e","list_key":"jeeb-prohibited-items","category":"old","keyword":"archived","description":null,"severity":"block","language":"en","active":false,"created_at":"2026-08-01T00:00:00Z","updated_at":"2026-08-03T00:00:00Z","archived_at":"2026-08-03T00:00:00Z"}]}"""));
        var store = Client(handler);

        var catalog = await store.GetActiveCatalogAsync(CancellationToken.None);

        catalog.Version.Should().Be(version, "the owner tag is opaque and must not be replaced by revision 91 or an updated_at timestamp");
        catalog.Items.Should().ContainSingle();
        catalog.Items[0].Name.Should().Be("kitchen knife");
        catalog.Items[0].Description.Should().Be("Sharp kitchen implement");
        catalog.Items[0].Severity.Should().Be(ProhibitedSeverity.Warn);
        handler.Requests.Select(request => request.PathAndQuery).Should().Equal(
            "/v1/moderation/catalogs/jeeb-prohibited-items/versions",
            "/v1/moderation/catalogs/jeeb-prohibited-items/versions/legacy%2F2026-08-09T23%3A59%3A58.1234567%2B00%3A00");
    }

    [Fact]
    public async Task Acknowledgement_Uses_Exact_Generic_Identity_And_Raw_User_Subject()
    {
        const string userId = "user/42";
        const string version = "opaque/v 7";
        var handler = new RecordingHandler((request, _) => Json(HttpStatusCode.OK,
            $$"""{"catalog_key":"jeeb-prohibited-items","consumer_key":"jeeb-gateway","subject_ref":"{{userId}}","version_tag":"{{version}}","acknowledged_at":"2026-08-10T10:11:12Z"}"""));
        var store = Client(handler);

        var existing = await store.GetAcknowledgmentAsync(userId, version, CancellationToken.None);
        var written = await store.AcknowledgeAsync(userId, version, CancellationToken.None);

        existing.Should().NotBeNull();
        existing!.UserId.Should().Be(userId);
        written.Version.Should().Be(version);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].PathAndQuery.Should().Be(
            "/v1/moderation/acknowledgements?catalog_key=jeeb-prohibited-items&consumer_key=jeeb-gateway&subject_ref=user%2F42&version_tag=opaque%2Fv%207");
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        body.RootElement.GetProperty("catalog_key").GetString().Should().Be(JeebModerationList.ListKey);
        body.RootElement.GetProperty("consumer_key").GetString().Should().Be("jeeb-gateway");
        body.RootElement.GetProperty("subject_ref").GetString().Should().Be(userId);
        body.RootElement.GetProperty("version_tag").GetString().Should().Be(version);
    }

    [Fact]
    public async Task CurrentAcknowledgement_Uses_Owner_Current_Query_And_Returns_Null_When_NotAcknowledged()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK,
            """{"catalog_key":"jeeb-prohibited-items","consumer_key":"jeeb-gateway","subject_ref":"raw-user-id","current_version_tag":"opaque-v9","acknowledged":false}"""));
        var store = Client(handler);

        var result = await store.GetAcknowledgmentAsync("raw-user-id", CancellationToken.None);

        result.Should().BeNull();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].PathAndQuery.Should().Be(
            "/v1/moderation/catalogs/jeeb-prohibited-items/acknowledgements/current?consumer_key=jeeb-gateway&subject_ref=raw-user-id");
    }

    [Fact]
    public async Task Admin_Create_And_Update_Map_Name_Description_Severity_And_Owner_Routes()
    {
        var handler = new RecordingHandler((request, index) => Json(
            index == 0 ? HttpStatusCode.Created : HttpStatusCode.OK,
            $$"""{"id":"{{ItemId}}","list_key":"jeeb-prohibited-items","category":"weapon","keyword":"knife","description":"owner-preserved","severity":"warn","language":"en","active":true,"created_by":"legacy-creator","updated_by":"admin-actor","created_at":"2026-08-01T00:00:00Z","updated_at":"2026-08-10T00:00:00Z"}"""));
        var store = Client(handler);

        var created = await store.CreateAsync(new ProhibitedItemCreate
        {
            Name = " knife ",
            Category = "weapon",
            Description = "owner-preserved",
            Severity = ProhibitedSeverity.Warn,
        }, "admin-actor", CancellationToken.None);
        var updated = await store.UpdateAsync(ItemId, new ProhibitedItemPatch
        {
            Description = "owner-preserved",
            Active = true,
        }, "admin-actor", CancellationToken.None);

        created.Description.Should().Be("owner-preserved");
        created.CreatedBy.Should().Be("legacy-creator");
        created.UpdatedBy.Should().Be("admin-actor");
        updated.Should().NotBeNull();
        handler.Requests[0].PathAndQuery.Should().Be(
            "/v1/moderation/admin/prohibited-items?list_key=jeeb-prohibited-items");
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[1].PathAndQuery.Should().Be(
            $"/v1/moderation/admin/prohibited-items/{ItemId}");

        using var createBody = JsonDocument.Parse(handler.Requests[0].Body!);
        createBody.RootElement.GetProperty("keyword").GetString().Should().Be("knife");
        createBody.RootElement.GetProperty("description").GetString().Should().Be("owner-preserved");
        createBody.RootElement.GetProperty("severity").GetString().Should().Be("warn");
        createBody.RootElement.GetProperty("actor_ref").GetString()
            .Should().Be("admin-actor");
        createBody.RootElement.TryGetProperty("admin_user_id", out _).Should().BeFalse();

        using var updateBody = JsonDocument.Parse(handler.Requests[1].Body!);
        updateBody.RootElement.GetProperty("description").GetString().Should().Be("owner-preserved");
        updateBody.RootElement.GetProperty("active").GetBoolean().Should().BeTrue();
        updateBody.RootElement.GetProperty("actor_ref").GetString()
            .Should().Be("admin-actor");
        updateBody.RootElement.TryGetProperty("keyword", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Stable_Owner_Duplicate_Reason_Maps_To_Gateway_Duplicate_Conflict()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Conflict,
            """{"error":"CATALOG_KEYWORD_DUPLICATE","message":"Catalog keyword already exists"}"""));
        var store = Client(handler);

        var act = () => store.CreateAsync(new ProhibitedItemCreate
        {
            Name = "knife",
            Category = "weapon",
        }, "admin", CancellationToken.None);

        await act.Should().ThrowAsync<DuplicateProhibitedItemNameException>()
            .WithMessage("*knife*");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Generic_Owner_Conflict_Is_Not_Mislabeled_As_A_Duplicate()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Conflict,
            """{"error":"CONFLICT","message":"Catalog changed concurrently"}"""));
        var store = Client(handler);

        var act = () => store.CreateAsync(new ProhibitedItemCreate
        {
            Name = "knife",
            Category = "weapon",
        }, "admin", CancellationToken.None);

        await act.Should().ThrowExactlyAsync<ProhibitedCatalogConflictException>()
            .WithMessage("Catalog changed concurrently");
    }

    [Fact]
    public async Task Atomic_Acknowledgement_Maps_Stable_StaleVersion_Conflict()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Conflict,
            """{"error":"CATALOG_VERSION_STALE","message":"Catalog version is no longer current"}"""));
        var store = Client(handler);

        var act = () => store.AcknowledgeAsync(
            "raw-user",
            "opaque-old-version",
            CancellationToken.None);

        var exception = await act.Should()
            .ThrowExactlyAsync<StaleProhibitedCatalogVersionException>();
        exception.Which.Version.Should().Be("opaque-old-version");
    }

    [Fact]
    public async Task Acknowledge_Endpoint_Returns_409_When_Catalog_Changes_During_Atomic_Write()
    {
        var store = new StaleAcknowledgementStore("opaque-current");
        var controller = new ProhibitedItemsController(store)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "raw-user")],
                        "test")),
                },
            },
        };

        var result = await controller.Acknowledge(
            new ProhibitedItemsAcknowledgeRequest { Version = "opaque-current" },
            CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeOfType<ProblemDetails>()
            .Which.Status.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Missing_Current_Opaque_Tag_Is_Rejected_Instead_Of_Using_Numeric_Revision()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK,
            """{"catalog_key":"jeeb-prohibited-items","current_revision":41,"current_version_tag":null,"versions":[]}"""));
        var store = Client(handler);

        var act = () => store.GetActiveCatalogAsync(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*missing or mismatched current catalog version*");
        handler.Requests.Should().ContainSingle("the adapter must fail before attempting an unpinned current read");
    }

    [Fact]
    public async Task Mismatched_Acknowledgement_Subject_Is_Rejected_FailClosed()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK,
            """{"catalog_key":"jeeb-prohibited-items","consumer_key":"jeeb-gateway","subject_ref":"another-user","version_tag":"opaque-v1","acknowledged_at":"2026-08-10T10:11:12Z"}"""));
        var store = Client(handler);

        var act = () => store.GetAcknowledgmentAsync(
            "expected-user",
            "opaque-v1",
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*mismatched acknowledgement identity*");
    }

    [Fact]
    public async Task ModerationGate_Scans_The_Same_VersionPinned_Snapshot()
    {
        var item = new ProhibitedItem
        {
            Id = ItemId,
            Name = "knife",
            Category = "weapon",
            Severity = ProhibitedSeverity.Warn,
            Active = true,
            CreatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
        };
        var store = new PinnedCatalogStore(
            new ProhibitedCatalogSnapshot([item], "opaque-owner-version"));
        var scanner = new ProhibitedItemScanner(store, new InMemorySynonymRegistry());

        var outcome = await new ModerationGate(store, scanner)
            .EvaluateAsync("a knife", CancellationToken.None);

        outcome.Version.Should().Be("opaque-owner-version");
        outcome.Scan.Matches.Should().ContainSingle(match => match.ItemId == ItemId);
    }

    private static BanServiceProhibitedItemsStore Client(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://ban.test/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                body));
            return response(request, Requests.Count - 1);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string PathAndQuery, string? Body);

    private sealed class PinnedCatalogStore(ProhibitedCatalogSnapshot snapshot)
        : IProhibitedItemsStore
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
            ProhibitedItemCreate input,
            string adminUserId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<ProhibitedItem?> UpdateAsync(
            string id,
            ProhibitedItemPatch patch,
            string adminUserId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<UserAcknowledgment?> GetAcknowledgmentAsync(
            string userId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<UserAcknowledgment> AcknowledgeAsync(
            string userId,
            string version,
            CancellationToken ct) => throw new NotSupportedException();

        // gwdbx W3-03 ack-ledger enumeration: importer-only, never exercised by these fixtures.
        public Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(
            int page,
            int pageSize,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StaleAcknowledgementStore(string version)
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
            ProhibitedItemCreate input,
            string adminUserId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<ProhibitedItem?> UpdateAsync(
            string id,
            ProhibitedItemPatch patch,
            string adminUserId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<UserAcknowledgment?> GetAcknowledgmentAsync(
            string userId,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<UserAcknowledgment> AcknowledgeAsync(
            string userId,
            string acknowledgedVersion,
            CancellationToken ct) => throw new StaleProhibitedCatalogVersionException(
                acknowledgedVersion,
                "catalog changed during write");

        // gwdbx W3-03 ack-ledger enumeration: importer-only, never exercised by these fixtures.
        public Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(
            int page,
            int pageSize,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
