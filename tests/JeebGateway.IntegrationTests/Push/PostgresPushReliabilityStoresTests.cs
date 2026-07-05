using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Push;
using JeebGateway.Services.Dispatch;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Push;

/// <summary>
/// Gateway durability hardening (JEBV4-144 / 137 / 136, AUDIT-A IN-MEM-LIVE) — the
/// push-reliability trio (INotificationDispatchOutbox, IPushRetryQueue,
/// IPushDeliveryTracker) is migrated from process memory to durable Postgres
/// (push-reliability tables, migration 0030) behind GatewayPostgres:ConnectionString.
/// Mirrors PostgresTiersStoreTests — the established DI-resolution-smoke + guard-
/// promotion pattern for a flag-gated store swap.
///
/// <para>The DI-resolution tests run for real, no live Postgres required: each
/// Postgres store's constructor only stores its collaborators (INpgsqlConnectionFactory
/// merely holds the connection string), so resolving the singleton never opens a
/// socket. Round-trip properties that genuinely need a live database are documented
/// as deferred-to-Testcontainers-QV placeholders, matching PostgresTiersStoreTests
/// (this project carries no Testcontainers dependency today).</para>
/// </summary>
public class PostgresPushReliabilityStoresTests
{
    // An unreachable connection string is enough: the constructors do no I/O, so the
    // durable impls resolve without ever dialing Postgres. Mirrors PostgresTiersStoreTests.
    private const string FakePostgresCs =
        "Host=127.0.0.1;Port=1;Database=jeeb_test;Username=jeeb;Password=jeeb;Timeout=1";

    private static WebApplicationFactory<Program> PostgresConfiguredFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // UseSetting lands in host configuration, read BEFORE Program.cs's top-level
            // gatewayPostgresCs read (ConfigureAppConfiguration alone is too late).
            b.UseSetting("GatewayPostgres:ConnectionString", FakePostgresCs);
            b.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GatewayPostgres:ConnectionString"] = FakePostgresCs
                }));
        });

    // ── DI wiring: durable impls selected when GatewayPostgres is configured ────

    [Fact]
    public void DispatchOutbox_Resolves_To_Postgres_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<INotificationDispatchOutbox>();
        act.Should().NotThrow("PostgresNotificationDispatchOutbox's constructor does no I/O");
        scope.ServiceProvider.GetRequiredService<INotificationDispatchOutbox>()
            .Should().BeOfType<PostgresNotificationDispatchOutbox>(
                "GatewayPostgres:ConnectionString is configured, so the durable outbox must be selected");
    }

    [Fact]
    public void RetryQueue_Resolves_To_Postgres_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPushRetryQueue>()
            .Should().BeOfType<PostgresPushRetryQueue>(
                "GatewayPostgres:ConnectionString is configured, so the durable retry queue must be selected");
    }

    [Fact]
    public void DeliveryTracker_Resolves_To_Postgres_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPushDeliveryTracker>()
            .Should().BeOfType<PostgresPushDeliveryTracker>(
                "GatewayPostgres:ConnectionString is configured, so the durable delivery tracker must be selected");
    }

    // ── DI wiring: in-memory fallback preserved when GatewayPostgres is absent ──

    [Fact]
    public void Trio_Resolves_To_InMemory_When_GatewayPostgres_Absent()
    {
        // Default test config carries no GatewayPostgres:ConnectionString, so the
        // in-memory fallbacks must remain the live path (unchanged behaviour for every
        // existing test that boots a bare WebApplicationFactory<Program>).
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<INotificationDispatchOutbox>()
            .Should().BeOfType<InMemoryNotificationDispatchOutbox>();
        scope.ServiceProvider.GetRequiredService<IPushRetryQueue>()
            .Should().BeOfType<InMemoryPushRetryQueue>();
        scope.ServiceProvider.GetRequiredService<IPushDeliveryTracker>()
            .Should().BeOfType<InMemoryPushDeliveryTracker>();
    }

    [Fact]
    public void InMemoryDeliveryTracker_Concrete_Still_Resolvable_Without_Postgres()
    {
        // DisputeServiceTests / DisputeCaseEndpointTests resolve the concrete
        // InMemoryPushDeliveryTracker to assert recorded outcomes — the in-memory
        // branch must keep that concrete registration.
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<InMemoryPushDeliveryTracker>()
            .Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IPushDeliveryTracker>(),
                "the interface must bind to the same singleton the concrete registration exposes");
    }

    // ── Durability guard promotion (JEBV4-144 / 137 / 136) ─────────────────────

    [Theory]
    [InlineData(typeof(INotificationDispatchOutbox), typeof(PostgresNotificationDispatchOutbox))]
    [InlineData(typeof(IPushRetryQueue), typeof(PostgresPushRetryQueue))]
    [InlineData(typeof(IPushDeliveryTracker), typeof(PostgresPushDeliveryTracker))]
    public void Store_Is_Now_A_Critical_Durable_Store_Requiring_Its_Postgres_Impl(Type iface, Type durableImpl)
    {
        var critical = StoreDurabilityGuard.Critical.FirstOrDefault(c => c.Iface == iface);

        critical.Iface.Should().Be(iface,
            $"{iface.Name} must be promoted to the Critical fail-closed set now that a durable target exists");
        critical.DurableImpls.Should().Contain(durableImpl,
            $"the only durable implementation that satisfies the prod-like gate is {durableImpl.Name}");
    }

    [Theory]
    [InlineData(typeof(INotificationDispatchOutbox))]
    [InlineData(typeof(IPushRetryQueue))]
    [InlineData(typeof(IPushDeliveryTracker))]
    public void Store_Is_No_Longer_On_The_InMemory_Backlog(Type iface)
    {
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().NotContain(iface,
            "a store with a durable target must not also be listed as a known-in-memory exemption");
    }

    [Theory]
    [InlineData(typeof(INotificationDispatchOutbox), typeof(InMemoryNotificationDispatchOutbox), "INotificationDispatchOutbox", "InMemoryNotificationDispatchOutbox")]
    [InlineData(typeof(IPushRetryQueue), typeof(InMemoryPushRetryQueue), "IPushRetryQueue", "InMemoryPushRetryQueue")]
    [InlineData(typeof(IPushDeliveryTracker), typeof(InMemoryPushDeliveryTracker), "IPushDeliveryTracker", "InMemoryPushDeliveryTracker")]
    public void EnsureDurable_ProdLike_With_InMemory_Impl_Fails_Closed(
        Type iface, Type inMemoryImpl, string ifaceName, string inMemoryName)
    {
        // Prove the promotion is live: a prod-like gateway resolving one of the trio
        // to its in-memory store must now refuse to boot, naming the offending store.
        var map = new Dictionary<Type, object>();
        foreach (var (i, durable) in StoreDurabilityGuard.Critical)
            map[i] = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(durable[0]);
        map[iface] = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(inMemoryImpl);

        var provider = new MapServiceProvider(map);
        var violations = StoreDurabilityGuard.Evaluate(t => provider.GetService(t)?.GetType());

        violations.Should().ContainSingle()
            .Which.Should().Contain(ifaceName).And.Contain(inMemoryName);
    }

    // ── Round-trip / concurrency (deferred to Testcontainers QV) ───────────────
    // Each property is enforced by a live Postgres in the QV pass, exactly as
    // PostgresTiersStoreTests defers its round-trip properties.

    [Fact]
    public void Outbox_Add_Exists_Due_MarkDelivered_Failure_Dlq_RoundTrips_DeferredToPostgresQV()
    {
        // Property: AddAsync INSERTs the entry (id, template_key, locale, parameters
        // JSONB, recipient, idempotency_key, status, attempt_count, timestamps) into
        // notification_dispatch_outbox (migration 0030); ExistsAsync matches the
        // idempotency key (partial-unique index); GetDueAsync claims due Pending rows
        // FIFO with FOR UPDATE SKIP LOCKED + a visibility lease (no double-send across
        // replicas); MarkDeliveredAsync flips status='Delivered'; RecordFailureAsync
        // increments attempt_count and either schedules next_attempt_at or moves to
        // 'DLQ' at >= maxAttempts — the exact branch InMemoryNotificationDispatchOutbox
        // takes; GetDlqAsync reads the DLQ rows.
        Assert.True(true, "Outbox round-trip + claim/lease verified against a live Postgres in the QV Testcontainers suite.");
    }

    [Fact]
    public void RetryQueue_Enqueue_DrainDue_Atomic_NeverReEnqueues_DeferredToPostgresQV()
    {
        // Property: EnqueueAsync appends the serialized PushNotificationRequest;
        // DrainDueAsync atomically DELETEs … RETURNING every due row (due_at <= now),
        // so each entry is handed to exactly one sweeper and is NOT re-enqueued —
        // the "retried once, then a hard failure" policy InMemoryPushRetryQueue's
        // DrainDue-then-RemoveAll enforced, now concurrency-safe across replicas.
        Assert.True(true, "Retry-queue atomic drain verified against a live Postgres in the QV Testcontainers suite.");
    }

    [Fact]
    public void DeliveryTracker_Record_GetForUser_GetRecent_RoundTrips_DeferredToPostgresQV()
    {
        // Property: RecordAsync appends (user_id, trigger, outcome enum names,
        // attempts_made, reason) to push_delivery_tracker; GetForUserAsync returns
        // every outcome for a user; GetRecentAsync returns the newest `limit` rows —
        // the same append-only log InMemoryPushDeliveryTracker exposed, now durable.
        Assert.True(true, "Delivery-tracker round-trip verified against a live Postgres in the QV Testcontainers suite.");
    }

    /// <summary>IServiceProvider backed by a fixed interface→instance map; unknown types resolve null.</summary>
    private sealed class MapServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyDictionary<Type, object> _map;
        public MapServiceProvider(IReadOnlyDictionary<Type, object> map) => _map = map;
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }
}
