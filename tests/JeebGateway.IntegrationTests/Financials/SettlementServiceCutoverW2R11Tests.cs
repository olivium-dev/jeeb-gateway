using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Extensions;
using JeebGateway.Financials;
using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// gwdbx W2-R11 — the gateway no longer owns settlement rows: settlement-service does. Four claims:
/// the settle legs reach the CLIENT keyed by delivery id and a duplicate is idempotent end-to-end;
/// an unreachable settlement-service still lets a handover commit with no 5xx; the readiness probe
/// tells the truth both ways; and the deleted local implementation is really gone.
/// </summary>
public class SettlementServiceCutoverW2R11Tests
{
    private const string RecipientPhone = "+9613123456";
    private const string TenantApplicationId = "17f6f47f-4047-4f1e-bac2-632a5eaa9a46";
    private const string ValidCode = "1234";
    private const decimal AcceptedFee = 2_000_000m;

    // ── A. Settle reaches the client, keyed by delivery id, idempotently ────

    [Fact]
    public async Task A1_Completion_Settles_Through_The_Client_Keyed_By_Delivery_Id()
    {
        var client = new RecordingSettlementClient();
        await using var factory = CompletionFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider(), client);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        client.Settles.Should().Contain(c => c.DeliveryId == deliveryId,
            "the completion leg must settle through settlement-service, keyed by the delivery id");
        client.Settles.Should().Contain(c => c.DeliveryId == deliveryId && c.GrossAmount == AcceptedFee,
            "the COD amount stays server-authoritative (BR-16)");
    }

    [Fact]
    public async Task A2_Duplicate_Settle_Is_Idempotent_End_To_End_One_Row_One_Id()
    {
        var client = new RecordingSettlementClient();
        await using var factory = CompletionFactory(SuccessfulVerifyClient(), new CapturingLoggerProvider(), client);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });
        var replay = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        client.Rows.Values.Count(r => r.DeliveryId == deliveryId).Should().Be(1,
            "settle is idempotent on the delivery id — a duplicate must never mint a second row");

        var receipt = await jeeber.GetAsync($"/deliveries/{deliveryId}/receipt");
        receipt.StatusCode.Should().Be(HttpStatusCode.OK);
        (await receipt.Content.ReadFromJsonAsync<ReceiptResponse>())!.SettlementId
            .Should().Be(client.Rows.Values.Single(r => r.DeliveryId == deliveryId).Id);
    }

    // ── B. An unreachable settlement-service never 5xx's a committed handover ─

    [Fact]
    public async Task B1_Settlement_Service_Unreachable_Completion_Still_Reaches_Done_With_No_5xx()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CompletionFactory(
            SuccessfulVerifyClient(), logs, new UnreachableSettlementClient());
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        var jeeber = ClientFor(factory, jeeberId, "driver");
        var verify = await jeeber.PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        ((int)verify.StatusCode).Should().BeLessThan(500,
            "a settlement hiccup must never turn a committed handover into a 5xx");
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        (await verify.Content.ReadAsStringAsync()).Should().Contain("Done");
        SwallowedSettlementLines(logs).Should().ContainSingle().Which.Should().Contain(deliveryId);
    }

    [Fact]
    public async Task B2_Customer_Patch_Leg_Also_Commits_Done_When_Settlement_Service_Is_Down()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CompletionFactory(
            DoneTransitionClient(), logs, new UnreachableSettlementClient());
        var (deliveryId, _) = await SeedAtDoorWithFeeAsync(factory);

        var client = ClientFor(factory, "w2r11-client-" + Guid.NewGuid(), "customer");
        var patch = await client.PatchAsync(
            $"/v1/deliveries/{deliveryId}/status", JsonContent.Create(new { to = "Done" }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        SwallowedSettlementLines(logs).Should().ContainSingle().Which.Should().Contain(deliveryId);
    }

    // ── C. The readiness probe tells the truth both ways ────────────────────

    [Fact]
    public async Task C1_Readiness_Reports_Unhealthy_When_Settlement_Service_Is_Down()
    {
        var report = await ProbeSettlementServiceAsync($"http://127.0.0.1:{ClosedPort()}");

        report.Status.Should().Be(HealthStatus.Unhealthy,
            "settlement-service owns the money rows — there is no local fallback left");
    }

    [Fact]
    public async Task C2_Readiness_Reports_Healthy_When_Settlement_Service_Is_Up()
    {
        await using var upstream = await StubSettlementServiceAsync();

        var report = await ProbeSettlementServiceAsync(upstream.Urls.Single());

        report.Status.Should().Be(HealthStatus.Healthy);
        upstream.Hits.Should().Contain("/health/ready",
            "the probe must hit the readiness path settlement-service actually serves");
    }

    [Fact]
    public void C3_The_A9_Roster_Is_20_And_Names_Settlement_Service()
    {
        // W2-R11 pre-announced 19 -> 20 (A9); W5 retirements took it to 18, D1 added role-service
        // (19), O8 retired role-service (18), and 2026-08-23 added notification-credential (19).
        // C4 is the positive control that the declared list still matches what the code registers.
        GatewayHealthRoster.ExpectedReadyCount.Should().Be(19,
            "A9 added settlement-service, W5 retired two probes, O8 retired role-service, "
            + "and the 608debf outage added the notification-credential check");
        GatewayHealthRoster.Ready.Should().HaveCount(GatewayHealthRoster.ExpectedReadyCount);
        GatewayHealthRoster.Ready.Should().Contain("settlement-service");
        GatewayHealthRoster.Ready.Should().NotContain("role-service");
    }

    [Fact]
    public void C4_The_Declared_Roster_Matches_What_The_Code_Registers()
    {
        // Positive control for C3: a hand-written list nobody checks is folklore. Register the
        // probes with every BaseUrl set and assert the registered names ARE the declared subset.
        var settings = GatewayHealthRoster.DownstreamProbes
            .ToDictionary(p => p.BaseUrlKey, p => (string?)"http://127.0.0.1:1");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDownstreamHealthChecks(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new StubEnvironment("Production"));

        var registered = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Select(r => r.Name).ToArray();

        registered.Should().BeEquivalentTo(GatewayHealthRoster.DownstreamProbes.Select(p => p.Name));
    }

    [Fact]
    public async Task C5_Staging_Registers_An_Unhealthy_Settlement_Check_When_Config_Is_Missing()
    {
        var report = await ProbeStagingSettlementServiceAsync(
            new Dictionary<string, string?>());

        report.Status.Should().Be(HealthStatus.Unhealthy,
            "the mandatory owner must not disappear from staging readiness when config is omitted");
    }

    [Fact]
    public async Task C6_Staging_Readiness_Requires_The_Mounted_Service_Credential()
    {
        await using var upstream = await StubSettlementServiceAsync();
        var report = await ProbeStagingSettlementServiceAsync(new Dictionary<string, string?>
        {
            [SettlementServiceOptions.BaseUrlKey] = upstream.Urls.Single(),
        });

        report.Status.Should().Be(HealthStatus.Unhealthy);
        upstream.Hits.Should().BeEmpty(
            "credential validation happens before the gateway calls the owner");
    }

    [Fact]
    public async Task C7_Staging_Readiness_Authenticates_The_Exact_Owner_Probe()
    {
        await using var upstream = await StubSettlementServiceAsync();
        var directory = Directory.CreateTempSubdirectory("settlement-readiness-token");
        try
        {
            var token = new string('s', 40);
            var path = Path.Combine(directory.FullName, "token");
            await File.WriteAllTextAsync(path, token + "\n");
            var report = await ProbeStagingSettlementServiceAsync(new Dictionary<string, string?>
            {
                [SettlementServiceOptions.BaseUrlKey] = upstream.Urls.Single(),
                [SettlementServiceOptions.ApiTokenFileKey] = path,
            });

            report.Status.Should().Be(HealthStatus.Healthy);
            upstream.Hits.Should().Contain("/health/ready");
            upstream.Authorizations.Should().Contain("Bearer " + token);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void C8_Staging_Options_Reject_Plaintext_Or_Missing_Settlement_Wiring()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDownstreamClients(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SettlementServiceOptions.BaseUrlKey] = "http://settlement.test/",
                [$"{SettlementServiceOptions.SectionName}:ApiToken"] = new string('p', 40),
            }).Build(),
            new StubEnvironment("Staging"));

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<SettlementServiceOptions>>().Value;

        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*ApiTokenFile*");
    }

    // ── D. The local implementation is gone, and nothing references it ──────

    [Fact]
    public void D1_The_Deleted_Settlement_Types_Are_Absent_From_The_Assembly()
    {
        var assembly = typeof(SettlementServiceClient).Assembly;
        var deleted = new[]
        {
            "ISettlementStore", "ISettlementBatchStore", "ISettlementEnqueueStore",
            "ISettlementLedgerClient", "InMemorySettlementStore", "InMemorySettlementEnqueueStore",
            "PostgresSettlementStore", "PostgresSettlementEnqueueStore", "PostgresSettlementLedgerClient",
            "InMemorySettlementLedgerClient", "NullSettlementStore", "NullSettlementBatchStore",
            "NullSettlementEnqueueStore", "NullSettlementLedgerClient", "SettlementStoreRetiredException",
            "WeeklySettlementBatch", "WeeklySettlementOptions", "SettlementLedgerReconciler",
            "SettlementLedgerReconcilerOptions", "ICodSettlementLedger", "InProcessCodSettlementLedger",
            "WalletApiSettlementLedgerClient", "CodWalletMirrorReconciler", "CodWalletMirrorOptions",
            "AdminSettlementPortalFilter", "SettlementBatch",
        };

        var survivors = assembly.GetTypes()
            .Select(t => t.Name)
            .Where(n => deleted.Contains(n, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        survivors.Should().BeEmpty("W2-R11 deletes the local settlement implementation outright");
    }

    [Fact]
    public void D2_The_Client_Is_The_Only_Settlement_Seam_In_The_Container()
    {
        using var factory = new WebApplicationFactory<Program>();

        factory.Services.GetRequiredService<ISettlementServiceClient>()
            .Should().BeOfType<SettlementServiceClient>();
        factory.Services.GetRequiredService<ISettlementService>().Should().NotBeNull();
    }

    [Fact]
    public void D3_The_Gateway_Never_Binds_The_Settlement_Admin_Token()
    {
        // A leaked gateway token must not be able to pay anyone: the admin scope is not a
        // configurable gateway key, so there is nothing to accidentally populate at deploy.
        typeof(SettlementServiceOptions).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "BaseUrl", "ApiToken", "ApiTokenFile", "HasServiceCredential" });
    }

    // ── E. Review hardening: the money surfaces the cutover narrowed ────────

    [Fact]
    public async Task E1_Earnings_Read_Pages_Past_The_Upstream_Limit_Instead_Of_Under_Counting()
    {
        // The deleted PostgresSettlementStore.ListByJeeberAsync had NO LIMIT. Upstream clamps
        // GET /settlements to 200 a page, so past 200 settlements a jeeber silently lost money.
        const int total = 250;
        var holderId = Guid.NewGuid().ToString();
        await using var upstream = await StubSettlementListAsync(holderId, total);
        var client = RealSettlementClient(upstream.BaseUrl);

        var rows = await client.ListAsync(new SettlementListQuery(HolderId: holderId), default);

        rows.Should().HaveCount(total, "one upstream page is not the whole window");
        rows.Select(r => r.DeliveryId).Distinct(StringComparer.Ordinal).Should().HaveCount(total);
        upstream.Requests.Count.Should().BeGreaterThan(1, "the nextCursor must actually be followed");
        upstream.Requests.Skip(1).Should()
            .OnlyContain(q => q.Contains("cursor=", StringComparison.Ordinal));

        // The jeeber-facing money screen is the real victim: gross must cover every row.
        var projection = await new EarningsAggregationService(client)
            .GetLifetimeProjectionAsync(holderId, default);

        projection.DeliveryCount.Should().Be(total);
        projection.Entries.Should().HaveCount(total);
        projection.Totals.Gross.Should().Be(Enumerable.Range(0, total).Sum(GrossFor));
    }

    [Fact]
    public async Task E2_Receipt_Read_Refuses_A_Pending_Settlement_Instead_Of_Emitting_A_Zero_Receipt()
    {
        // After a bounce the self-heal can record an amount-less PENDING intent (upstream stores
        // money as NULL while pending). Rendering that is a $0.00 receipt on a completed COD job.
        var settlements = new FakeSettlementServiceClient();
        await using var factory = CompletionFactory(
            new ConfigurableDeliveryClient(), new CapturingLoggerProvider(), settlements);
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        await settlements.SettleAsync(
            new SettlementSettleCommand(
                DeliveryId: deliveryId,
                HolderId: jeeberId,
                ClientId: Guid.NewGuid().ToString(),
                TierId: null,
                GrossAmount: null,
                PaymentMethod: SettlementService.PaymentMethodCash),
            default);
        settlements.Rows[deliveryId].State.Should().Be(SettlementState.PendingSettlement);

        var response = await ClientFor(factory, jeeberId, "driver")
            .GetAsync($"/deliveries/{deliveryId}/receipt");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a pending intent carries no money — there is no receipt to render yet");
        settlements.Rows[deliveryId].ReceiptGeneratedAt.Should().BeNull(
            "a pending row must not be stamped as if a receipt had been issued");
    }

    [Fact]
    public async Task E3_A_Swallowed_Settle_Increments_The_Dropped_Settle_Counter()
    {
        // Both completion legs swallow settlement faults so a handover can never 5xx. The only
        // trace was one ERROR line, and it promised a reconciler this step deleted.
        var logs = new CapturingLoggerProvider();
        await using var factory = CompletionFactory(
            SuccessfulVerifyClient(), logs, FakeSettlementServiceClient.Unreachable());
        var (deliveryId, jeeberId) = await SeedAtDoorWithFeeAsync(factory);

        using var counted = new CounterProbe("settlement.ledger.post_failures");
        var verify = await ClientFor(factory, jeeberId, "driver")
            .PostAsJsonAsync($"/deliveries/{deliveryId}/otp/verify", new { code = ValidCode });

        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        counted.Total.Should().BeGreaterThan(0,
            "a settle that did not land must be observable as more than a log line");

        var swallowed = SwallowedSettlementLines(logs);
        swallowed.Should().ContainSingle();
        swallowed.Single().Should().NotContain("reconcil",
            "the reconciler is deleted — the log must not promise a replay that never comes");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static decimal GrossFor(int index) => 100m + index;

    private static SettlementServiceClient RealSettlementClient(string baseUrl)
        => new(
            new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") },
            NullLogger<SettlementServiceClient>.Instance);

    /// <summary>Stands in for GET /settlements with the upstream paging contract: keyset cursor,
    /// created_at DESC, and nextCursor set only when the page came back full.</summary>
    private static async Task<StubList> StubSettlementListAsync(string holderId, int total)
    {
        var rows = Enumerable.Range(0, total)
            .Select(i => new
            {
                settlementId = Guid.NewGuid(),
                deliveryId = $"dlv-{i:D4}",
                holderId,
                clientId = Guid.NewGuid().ToString(),
                tierId = string.Empty,
                state = "settled",
                currency = SettlementService.CurrencyUsd,
                paymentMethod = SettlementService.PaymentMethodCash,
                grossAmount = GrossFor(i),
                commissionRate = 0.10m,
                commissionAmount = decimal.Round(GrossFor(i) * 0.10m, 2, MidpointRounding.AwayFromZero),
                settledAt = DateTimeOffset.UnixEpoch.AddMinutes(i),
                createdAt = DateTimeOffset.UnixEpoch.AddMinutes(i),
            })
            .OrderByDescending(r => r.createdAt)
            .ToArray();

        var seen = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/settlements", (HttpContext ctx, int? limit, string? cursor) =>
        {
            seen.Enqueue(ctx.Request.QueryString.Value ?? string.Empty);
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var offset = string.IsNullOrWhiteSpace(cursor) ? 0 : int.Parse(cursor);
            var page = rows.Skip(offset).Take(take).ToArray();
            var next = page.Length == take && page.Length > 0 ? (offset + take).ToString() : null;
            return Results.Ok(new { items = page, nextCursor = next });
        });
        await app.StartAsync();
        return new StubList(app, seen);
    }

    private sealed class StubList : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _seen;

        public StubList(WebApplication app, ConcurrentQueue<string> seen)
        {
            _app = app;
            _seen = seen;
            BaseUrl = app.Urls.First();
        }

        public string BaseUrl { get; }
        public IReadOnlyList<string> Requests => _seen.ToArray();

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Sums one gateway-meter counter for the lifetime of the probe.</summary>
    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public CounterProbe(string instrument)
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == BusinessOutcomeTelemetry.MeterName && i.Name == instrument)
                    l.EnableMeasurementEvents(i);
            };
            _listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref _total, v));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    private static async Task<HealthReport> ProbeSettlementServiceAsync(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDownstreamHealthChecks(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Services:Settlement:BaseUrl"] = baseUrl }).Build(),
            new StubEnvironment("Production"));

        await using var sp = services.BuildServiceProvider();
        return await sp.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "settlement-service", default);
    }

    private static async Task<HealthReport> ProbeStagingSettlementServiceAsync(
        IReadOnlyDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDownstreamHealthChecks(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new StubEnvironment("Staging"));

        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "settlement-service", default);
    }

    private static async Task<StubUpstream> StubSettlementServiceAsync()
    {
        var hits = new ConcurrentBag<string>();
        var authorizations = new ConcurrentBag<string?>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/health/ready", (HttpContext ctx) =>
        {
            hits.Add(ctx.Request.Path.Value!);
            authorizations.Add(ctx.Request.Headers.Authorization.ToString());
            return Results.Ok(new { status = "Healthy" });
        });
        await app.StartAsync();
        return new StubUpstream(app, hits, authorizations);
    }

    private sealed class StubUpstream : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public StubUpstream(
            WebApplication app,
            ConcurrentBag<string> hits,
            ConcurrentBag<string?> authorizations)
        {
            _app = app;
            Hits = hits;
            Authorizations = authorizations;
            Urls = app.Urls.ToArray();
        }

        public ConcurrentBag<string> Hits { get; }
        public ConcurrentBag<string?> Authorizations { get; }
        public IReadOnlyList<string> Urls { get; }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public StubEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>In-memory stand-in for settlement-service, honouring the upstream idempotency
    /// contract: one row per delivery id, a duplicate settle replays it.</summary>
    private sealed class RecordingSettlementClient : ISettlementServiceClient
    {
        public ConcurrentBag<SettlementSettleCommand> Settles { get; } = new();
        public ConcurrentDictionary<string, Settlement> Rows { get; } = new(StringComparer.Ordinal);

        public Task<SettlementSettleResult> SettleAsync(SettlementSettleCommand command, CancellationToken ct)
        {
            Settles.Add(command);
            var created = !Rows.ContainsKey(command.DeliveryId);
            var row = Rows.GetOrAdd(command.DeliveryId, _ => Build(command));
            return Task.FromResult(new SettlementSettleResult(row, created));
        }

        public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(deliveryId, out var row) ? row : null);

        public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(r => r.Id == settlementId));

        public Task<IReadOnlyList<Settlement>> ListAsync(SettlementListQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Settlement>>(Rows.Values
                .Where(r => query.HolderId is null || r.JeeberId == query.HolderId).ToArray());

        public Task<Settlement?> StampExternalRefAsync(
            string settlementId, string externalRef, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(r => r.Id == settlementId));

        public Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(r => r.Id == settlementId));

        public Task<decimal> SumNetEarningsAsync(
            string? holderId, IReadOnlyCollection<string>? states,
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => Task.FromResult(Rows.Values.Sum(r => r.GoodsCost - r.Commission));

        private static Settlement Build(SettlementSettleCommand c)
        {
            var breakdown = CommissionCalculator.Calculate(
                c.GrossAmount ?? 0m, CommissionCalculator.ResolveTier(c.TierId));
            return new Settlement
            {
                Id = Guid.NewGuid().ToString(),
                DeliveryId = c.DeliveryId,
                ClientId = c.ClientId,
                JeeberId = c.HolderId,
                TierId = c.TierId ?? string.Empty,
                GoodsCost = breakdown.GoodsCost,
                CommissionTier = breakdown.Tier,
                CommissionRate = breakdown.CommissionRate,
                Commission = breakdown.Commission,
                Insurance = breakdown.Insurance,
                Total = breakdown.Total,
                MinimumFeeApplied = breakdown.MinimumFeeApplied,
                Currency = SettlementService.CurrencyUsd,
                PaymentMethod = c.PaymentMethod,
                State = c.GrossAmount is null
                    ? SettlementState.PendingSettlement
                    : SettlementState.Settled,
                SettledAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private sealed class UnreachableSettlementClient : ISettlementServiceClient
    {
        private static SettlementServiceUnavailableException Down(string member)
            => new(member, "connection refused");

        public Task<SettlementSettleResult> SettleAsync(SettlementSettleCommand command, CancellationToken ct)
            => throw Down(nameof(SettleAsync));

        public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw Down(nameof(GetByDeliveryAsync));

        public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
            => throw Down(nameof(GetByIdAsync));

        public Task<IReadOnlyList<Settlement>> ListAsync(SettlementListQuery query, CancellationToken ct)
            => throw Down(nameof(ListAsync));

        public Task<Settlement?> StampExternalRefAsync(
            string settlementId, string externalRef, CancellationToken ct)
            => throw Down(nameof(StampExternalRefAsync));

        public Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, CancellationToken ct)
            => throw Down(nameof(MarkReceiptGeneratedAsync));

        public Task<decimal> SumNetEarningsAsync(
            string? holderId, IReadOnlyCollection<string>? states,
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
            => throw Down(nameof(SumNetEarningsAsync));
    }

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

    private static WebApplicationFactory<Program> CompletionFactory(
        ConfigurableDeliveryClient delivery,
        CapturingLoggerProvider logs,
        ISettlementServiceClient settlements)
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
                services.RemoveAll<ISettlementServiceClient>();
                services.AddSingleton(settlements);
            });
        });

    private static async Task<(string deliveryId, string jeeberId)> SeedAtDoorWithFeeAsync(
        WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = Guid.NewGuid().ToString();
        var jeeberId = Guid.NewGuid().ToString();

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
    // OA-21 (51a2677) added the provider-audience reads to IDeliveryServiceClient. This double's
    // subject is elsewhere; an empty audience is the neutral answer, not a simulated fault.
    public Task<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>> ListAvailableProvidersAsync(
        double? lat, double? lng, double? radiusKm,
        IReadOnlyCollection<string>? roles, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.AvailableProviderUpstream>());

    public Task<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
        System.DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>());

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
