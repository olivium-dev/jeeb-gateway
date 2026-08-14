using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Financials;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// gwdbx W2-R02 — the four gateway settlement tables are dropped (migration 0052) and the
/// writers are neutralised by the Null* stores. Three claims are proven here: the SELECTOR picks
/// Null in every prod-like wiring, a COMPLETION still reaches Done with no 5xx and emits exactly
/// one swallowed-settlement signal, and every caller-facing settlement READ answers 503
/// "retired" rather than a confident empty (the O10 ledger-drill lesson).
///
/// <para><b>NOT proven here, and it cannot be:</b> the migration's real double-apply behaviour.
/// That needs a live Postgres; .20 is off the network. The migration legs below are SOURCE
/// assertions on the file's shape, and they say so.</para>
/// </summary>
public class SettlementStoreRetiredW2R02Tests
{
    private const string FakeCs =
        "Host=127.0.0.1;Port=1;Database=jeeb_test;Username=jeeb;Password=jeeb;Timeout=1";

    // >=32 bytes so a Production boot clears JwtSigningKeyGuard and actually reaches the DI graph.
    private const string ProdJwtKey = "w2r02-production-boot-signing-key-32+ch";

    private const string RecipientPhone = "+9613123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";
    private const decimal AcceptedFee = 2_000_000m;

    // ── A. The selector ────────────────────────────────────────────────────

    [Fact]
    public void A1_ProdLike_With_GatewayPostgres_Resolves_Every_Settlement_Seam_To_Null()
    {
        using var factory = ProdFactory(FakeCs);
        var sp = factory.Services;

        sp.GetRequiredService<ISettlementStore>().Should().BeOfType<NullSettlementStore>(
            "migration 0052 dropped settlements — a Postgres store would 42P01 on every call");
        sp.GetRequiredService<ISettlementEnqueueStore>().Should().BeOfType<NullSettlementEnqueueStore>();
        sp.GetRequiredService<ISettlementBatchStore>().Should().BeOfType<NullSettlementBatchStore>();
        sp.GetRequiredService<ISettlementLedgerClient>().Should().BeOfType<NullSettlementLedgerClient>();
    }

    [Fact]
    public void A2_ProdLike_Without_GatewayPostgres_Still_Resolves_To_Null_Never_InMemory()
    {
        // This is the hole the G-08 roster removal opens: with the four entries gone, nothing
        // stops a prod-like zero-DSN boot from serving money out of a ConcurrentDictionary.
        using var factory = ProdFactory(gatewayPostgresCs: null);
        var sp = factory.Services;

        sp.GetRequiredService<ISettlementStore>().Should().BeOfType<NullSettlementStore>(
            "a zero-DSN prod-like boot must not fake settlements from process memory");
        sp.GetRequiredService<ISettlementEnqueueStore>().Should().BeOfType<NullSettlementEnqueueStore>();
        sp.GetRequiredService<ISettlementBatchStore>().Should().BeOfType<NullSettlementBatchStore>();
        sp.GetRequiredService<ISettlementLedgerClient>().Should().BeOfType<NullSettlementLedgerClient>();
    }

    [Fact]
    public void A3_Dev_With_GatewayPostgres_Resolves_To_Null_Because_The_Tables_Are_Gone()
    {
        // A dev box that ran db/apply.sh has no settlement tables either.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("GatewayPostgres:ConnectionString", FakeCs));

        factory.Services.GetRequiredService<ISettlementStore>().Should().BeOfType<NullSettlementStore>();
        factory.Services.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<NullSettlementLedgerClient>();
    }

    [Fact]
    public void A4_Dev_Without_GatewayPostgres_Keeps_The_InMemory_Stores()
    {
        // POSITIVE CONTROL for A1–A3: without it, a wiring that ALWAYS returned Null would
        // satisfy them without the selector selecting anything. Dev/Testing keeps the
        // in-memory stores so the settlement behaviour stays specified until W2-R11.
        using var factory = new WebApplicationFactory<Program>();
        var sp = factory.Services;

        sp.GetRequiredService<ISettlementStore>().Should().BeOfType<InMemorySettlementStore>();
        sp.GetRequiredService<ISettlementEnqueueStore>().Should().BeOfType<InMemorySettlementEnqueueStore>();
        sp.GetRequiredService<ISettlementLedgerClient>().Should().BeOfType<InMemorySettlementLedgerClient>();
    }

    // ── B. Completion: terminal state reached, one swallowed signal ─────────

    [Fact]
    public async Task B1_Otp_Verify_To_Done_Still_Returns_200_And_Logs_One_Swallowed_Settlement()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = RetiredStoreFactory(SuccessfulVerifyClient(), logs);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK,
            "a retired settlement store must never turn a committed handover into a 5xx");
        SwallowedSettlementLines(logs).Should().ContainSingle(
            "the step's verify clause expects exactly ONE swallowed-settlement log line")
            .Which.Should().Contain(deliveryId);
    }

    [Fact]
    public async Task B2_Customer_Patch_To_Done_Still_Returns_200_And_Logs_One_Swallowed_Settlement()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = RetiredStoreFactory(DoneTransitionClient(), logs);
        var (deliveryId, _) = await SeedAtDoorWithFeeAsync(factory);

        var client = ClientFor(factory, "w2r02-client-" + Guid.NewGuid(), "customer");
        var patch = await client.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status", JsonContent.Create(new { to = "Done" }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK, "the customer PATCH leg must still commit Done");
        SwallowedSettlementLines(logs).Should().ContainSingle().Which.Should().Contain(deliveryId);
    }

    [Fact]
    public async Task B3_With_A_Working_Store_There_Is_No_Swallowed_Settlement_Line()
    {
        // POSITIVE CONTROL for B1/B2: the signal must be caused by the retired store, not by
        // the harness. Same request, in-memory store, and the failure line must be absent.
        var logs = new CapturingLoggerProvider();
        await using var factory = WorkingStoreFactory(SuccessfulVerifyClient(), logs);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        SwallowedSettlementLines(logs).Should().BeEmpty(
            "with a store that answers, the completion settles and logs settlement.on_complete");
    }

    // ── C. Reads answer "gone", never a confident empty ─────────────────────

    [Fact]
    public async Task C1_Jeeber_Earnings_Returns_503_Retired_Not_A_Zeroed_Projection()
    {
        await using var factory = RetiredStoreFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider());
        var jeeber = ClientFor(factory, "w2r02-earnings-jeeber", "driver");

        var response = await jeeber.GetAsync("/v1/jeebers/me/earnings?period=week");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "an empty 200 would tell a jeeber they earned nothing; the truth is the store is gone");
        (await response.Content.ReadAsStringAsync())
            .Should().Contain(SettlementStoreRetiredException.ProblemType);
    }

    [Fact]
    public async Task C2_Admin_Settlements_List_Returns_503_Retired_Not_An_Empty_Page()
    {
        await using var factory = RetiredStoreFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider());
        var admin = ClientFor(factory, "w2r02-admin", "admin");

        var response = await admin.GetAsync("/admin/v1/settlements");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a CMS operator must never be shown a confident empty settlement screen");
        (await response.Content.ReadAsStringAsync())
            .Should().Contain(SettlementStoreRetiredException.ProblemType);
    }

    [Fact]
    public async Task C3_Admin_Settlement_Batches_Return_503_Retired()
    {
        await using var factory = RetiredStoreFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider());
        var admin = ClientFor(factory, "w2r02-admin", "admin");

        var response = await admin.GetAsync("/v1/admin/settlements/batches?status=open");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain(SettlementStoreRetiredException.ProblemType);
    }

    [Fact]
    public async Task C4_Receipt_Read_Returns_503_Retired_Not_404()
    {
        // 404 would say "this delivery has no receipt", which is a different and false claim.
        await using var factory = RetiredStoreFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider());
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);
        var jeeber = ClientFor(factory, jeeberId, "driver");

        var response = await jeeber.GetAsync($"/deliveries/{deliveryId}/receipt");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain(SettlementStoreRetiredException.ProblemType);
    }

    [Fact]
    public async Task C5_With_A_Working_Store_The_Same_Reads_Do_Not_503()
    {
        // POSITIVE CONTROL for C1–C4: without it, a gateway that 503'd everything would pass.
        await using var factory = WorkingStoreFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider());

        var jeeber = ClientFor(factory, "w2r02-earnings-jeeber", "driver");
        (await jeeber.GetAsync("/v1/jeebers/me/earnings?period=week")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var admin = ClientFor(factory, "w2r02-admin", "admin");
        (await admin.GetAsync("/admin/v1/settlements")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync("/v1/admin/settlements/batches?status=open")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // ── D. The sweeps stay quiet, deliberately ─────────────────────────────

    [Fact]
    public async Task D1_Background_Sweep_Reads_Return_Empty_So_The_Reconcilers_Do_Not_Flood()
    {
        var store = new NullSettlementStore();

        (await store.ListUnpostedLedgerAsync(100, default)).Should().BeEmpty(
            "the 60 s ledger reconciler would otherwise log an ERROR every minute forever");
        (await store.ListRecordedInWindowAsync(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, 500, default))
            .Should().BeEmpty();
        (await store.ListWalletUnmirroredAsync(DateTimeOffset.UtcNow.AddDays(-7), 100, default)).Should().BeEmpty();
        (await new NullSettlementBatchStore().ListUnsettledAsync(500, default)).Should().BeEmpty();
    }

    [Fact]
    public void D2_Every_Caller_Facing_Member_Faults_Including_The_Two_With_Default_Empty_Bodies()
    {
        ISettlementStore store = new NullSettlementStore();

        // ISettlementStore ships DEFAULT implementations returning empty/0 for these two. Not
        // overriding them would reintroduce the exact confident-empty this step forbids. The Null
        // members throw SYNCHRONOUSLY, so an Action (not a Func<Task>) is the honest assertion.
        Faults(() => store.ListPageForAdminAsync(new AdminSettlementPortalFilter(), 50, default));
        Faults(() => store.SumEarningsAsync(CodSettlementState.EarningsStates, default));
        Faults(() => store.GetByDeliveryAsync("d1", default));
        Faults(() => store.ListByJeeberAsync("j1", null, null, default));
        Faults(() => store.TryInsertAsync(new Settlement
        {
            Id = "s1", DeliveryId = "d1", ClientId = "c1", JeeberId = "j1", TierId = "standard",
            GoodsCost = 1m, CommissionTier = CommissionTier.Standard, CommissionRate = 0.1m,
            Commission = 0.1m, Insurance = 0m, Total = 1m, MinimumFeeApplied = false,
            Currency = "USD", PaymentMethod = "cash", State = SettlementState.Settled,
            SettledAt = DateTimeOffset.UtcNow,
        }, default));
    }

    private static void Faults(Action call) =>
        call.Should().Throw<SettlementStoreRetiredException>();

    // ── E. Migration 0052 — SOURCE shape only (no live DB; see class doc) ───

    [Fact]
    public void E1_Migration_0052_Is_Table_Scoped_Version_Inserting_And_Idempotent_Both_Ways()
    {
        var dir = Path.Combine(RepoRoot(), "db", "migrations");
        var path = Path.Combine(dir, "0052_drop_gateway_settlement_tables.sql");
        File.Exists(path).Should().BeTrue($"missing {path}");

        var sql = StripSqlComments(File.ReadAllText(path));

        sql.Should().Contain("INSERT INTO schema_migrations",
            "an unregistered migration re-runs on every deploy (G-18)");
        sql.Should().Contain("'0052_drop_gateway_settlement_tables'");
        sql.Should().Contain("to_regclass",
            "the already-dropped state (the owner dropped .20 by hand, A23) must be a skip, not an error");

        foreach (var table in new[]
                 { "settlements", "settlement_batches", "settlement_enqueue", "settlement_ledger_entries" })
        {
            sql.Should().Contain($"DROP TABLE IF EXISTS {table};",
                "IF EXISTS is mandatory, not optional, under A23");
        }

        sql.Should().NotContain("CASCADE",
            "RESTRICT (the default) on purpose — an unexpected dependent must fail the apply");
        Regex.Matches(sql, @"DROP\s+TABLE", RegexOptions.IgnoreCase).Count.Should().Be(4,
            "table-scoped: this file drops the settlement stack and nothing else");

        // settlements.batch_id REFERENCES settlement_batches(id) (0015), so the child goes first
        // or RESTRICT aborts the apply.
        sql.IndexOf("DROP TABLE IF EXISTS settlements;", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("DROP TABLE IF EXISTS settlement_batches;", StringComparison.Ordinal));
    }

    [Fact]
    public void E2_Migration_0052_Is_Numbered_Above_The_0039_Sentinel_And_Is_Unique()
    {
        var dir = Path.Combine(RepoRoot(), "db", "migrations");
        var names = Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).ToArray();

        names.Count(n => n!.StartsWith("0052", StringComparison.Ordinal)).Should().Be(1,
            "two migrations sharing a number is a deploy hazard");
        names.Select(n => n!.Split('_')[0]).Should().OnlyHaveUniqueItems();

        // 0038/0039 RAISE EXCEPTION when settlement_batches is absent and apply.sh re-runs every
        // file in lexicographic order, so a drop numbered below them breaks every later apply.
        // Assert the sentinels really do that — a tautological ordering check would prove nothing.
        foreach (var sentinel in names.Where(n =>
                     n!.StartsWith("0038", StringComparison.Ordinal) ||
                     n!.StartsWith("0039", StringComparison.Ordinal)))
        {
            var body = StripSqlComments(File.ReadAllText(Path.Combine(dir, sentinel!)));
            body.Should().Contain("RAISE EXCEPTION",
                $"{sentinel} is the sentinel this migration must sort AFTER");
            body.Should().Contain("settlement_batches");
            string.CompareOrdinal("0052_drop_gateway_settlement_tables.sql", sentinel).Should().BeGreaterThan(0,
                "apply.sh applies files in lexicographic order; the drop must run after the sentinel");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> SwallowedSettlementLines(CapturingLoggerProvider logs)
    {
        lock (logs.Records)
        {
            return logs.Records
                .Where(r => r.Message.Contains("settlement.on_complete_failed", StringComparison.Ordinal))
                .Select(r => r.Message)
                .ToList();
        }
    }

    private static WebApplicationFactory<Program> ProdFactory(string? gatewayPostgresCs)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Jwt:SigningKey", ProdJwtKey);
            b.UseSetting("UmJwt:SigningKey", ProdJwtKey);
            // The Redis boot guard checks the KEY is set (no socket); same recipe as
            // RedisFailClosedBootGuardTests' drill B, which boots this host green.
            b.UseSetting("Redis:ConnectionString", "127.0.0.1:6379");
            b.UseSetting("BffServices:RequiredInProduction", "false");
            // Narrow harness escape hatch: this factory inspects the DI graph, it does not
            // provision real state-service/Redis/upstream stores.
            b.UseSetting("StoreDurability:FailClosedDisabled", "true");
            if (gatewayPostgresCs is not null)
                b.UseSetting("GatewayPostgres:ConnectionString", gatewayPostgresCs);
        });

    /// <summary>The completion harness with the retired (Null) settlement seams injected.</summary>
    private static WebApplicationFactory<Program> RetiredStoreFactory(
        ConfigurableDeliveryClient delivery, CapturingLoggerProvider logs)
        => CompletionFactory(delivery, logs, services =>
        {
            services.RemoveAll<ISettlementStore>();
            services.AddSingleton<ISettlementStore, NullSettlementStore>();
            services.RemoveAll<ISettlementBatchStore>();
            services.AddSingleton<ISettlementBatchStore, NullSettlementBatchStore>();
            services.RemoveAll<ISettlementLedgerClient>();
            services.AddSingleton<ISettlementLedgerClient, NullSettlementLedgerClient>();
            services.RemoveAll<ISettlementEnqueueStore>();
            services.AddSingleton<ISettlementEnqueueStore, NullSettlementEnqueueStore>();
        });

    /// <summary>The identical harness on the shipped in-memory stores — the positive control.</summary>
    private static WebApplicationFactory<Program> WorkingStoreFactory(
        ConfigurableDeliveryClient delivery, CapturingLoggerProvider logs)
        => CompletionFactory(delivery, logs, _ => { });

    private static WebApplicationFactory<Program> CompletionFactory(
        ConfigurableDeliveryClient delivery,
        CapturingLoggerProvider logs,
        Action<IServiceCollection> overrideSettlement)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.UseSetting("Auth:Otp:ApplicationId", TenantApplicationId);
            builder.ConfigureServices(services =>
            {
                services.AddLogging(b => b.AddProvider(logs));
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new SilentOtpClient());
                services.RemoveAll<IConversationProvisioner>();
                services.AddSingleton<IConversationProvisioner>(new NoOpConversationProvisioner());
                overrideSettlement(services);
            });
        });

    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorWithFeeAsync(
        WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"w2r02-client-{Guid.NewGuid()}";
        var jeeberId = $"w2r02-jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the parcel",
            RecipientPhone = RecipientPhone
        }, default);
        (await store.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await store.TrySetAcceptedFeeAsync(created.Id, AcceptedFee, default)).Should().BeTrue();
        (await store.SetStatusAsync(created.Id, RequestStatus.AtDoor, default)).Should().BeTrue();
        return (created.Id, jeeberId);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static string StripSqlComments(string sql)
        => Regex.Replace(sql, @"^\s*--[^\n]*$", "", RegexOptions.Multiline);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test binary must be able to locate the repo root");
        return dir!.FullName;
    }

    private static ConfigurableDeliveryClient SuccessfulVerifyClient() => new()
    {
        VerifyOutcome = _ => new DeliveryHandoverVerifyResult
        {
            DeliveryId = "overwritten",
            Verified = true,
            Status = CanonicalDeliveryStatus.Done
        }
    };

    private static ConfigurableDeliveryClient DoneTransitionClient() => new()
    {
        TransitionTo = CanonicalDeliveryStatus.Done
    };

    private sealed class NoOpConversationProvisioner : IConversationProvisioner
    {
        public Task<string?> CreateBroadcastingConversationAsync(string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task CloseConversationAsync(string? conversationId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _parent;
            public CapturingLogger(CapturingLoggerProvider parent) => _parent = parent;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_parent.Records)
                {
                    _parent.Records.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    /// <summary>Delivery-service double: verify and canonical-transition return a fixed terminal
    /// status; every other member is loud so an unexpected call cannot pass silently.</summary>
    private sealed class ConfigurableDeliveryClient : IDeliveryServiceClient
    {
        public Func<bool, DeliveryHandoverVerifyResult> VerifyOutcome { get; init; }
            = _ => throw new DeliveryHandoverException((int)HttpStatusCode.Conflict, "not_at_door");

        public string? TransitionTo { get; init; }

        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
        {
            var r = VerifyOutcome(success);
            return Task.FromResult(new DeliveryHandoverVerifyResult
            {
                DeliveryId = deliveryId,
                Verified = r.Verified,
                Status = r.Status
            });
        }

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            if (TransitionTo is null) throw new NotSupportedException();
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = TransitionTo,
                TransitionedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult<DeliveryReadUpstream?>(new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                Status = CanonicalDeliveryStatus.Done,
                CreatedAt = DateTimeOffset.UtcNow
            });

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class SilentOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
