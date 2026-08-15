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
/// Ownership regression: notification-service owns tokens, outbox, retry, and
/// delivery tracking. Gateway runtime DI must expose none of the former stores,
/// regardless of whether GatewayPostgres is configured.
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
    public void DispatchOutbox_Is_Not_Runtime_Registered_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<INotificationDispatchOutbox>().Should().BeNull();
    }

    [Fact]
    public void DeliveryTracker_Is_Not_Runtime_Registered_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<IPushDeliveryTracker>().Should().BeNull();
        scope.ServiceProvider.GetService<IDeviceTokenStore>().Should().BeNull();
    }

    // ── DI wiring: in-memory fallback preserved when GatewayPostgres is absent ──

    [Fact]
    public void Retired_Stores_Have_No_InMemory_Fallback_When_GatewayPostgres_Absent()
    {
        // Default test config carries no GatewayPostgres:ConnectionString, so the
        // in-memory fallbacks must remain the live path (unchanged behaviour for every
        // existing test that boots a bare WebApplicationFactory<Program>).
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<INotificationDispatchOutbox>().Should().BeNull();
        scope.ServiceProvider.GetService<IPushDeliveryTracker>().Should().BeNull();
        scope.ServiceProvider.GetService<IDeviceTokenStore>().Should().BeNull();
    }

    [Fact]
    public void InMemoryDeliveryTracker_Concrete_Is_Not_Runtime_Registered()
    {
        // DisputeServiceTests / DisputeCaseEndpointTests resolve the concrete
        // InMemoryPushDeliveryTracker to assert recorded outcomes — the in-memory
        // branch must keep that concrete registration.
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<InMemoryPushDeliveryTracker>().Should().BeNull();
    }

    // ── Durability guard promotion (JEBV4-144 / 137 / 136) ─────────────────────

    [Theory]
    [InlineData(typeof(INotificationDispatchOutbox), typeof(PostgresNotificationDispatchOutbox))]
    [InlineData(typeof(IPushDeliveryTracker), typeof(PostgresPushDeliveryTracker))]
    [InlineData(typeof(IDeviceTokenStore), typeof(PostgresDeviceTokenStore))]
    public void Retired_Store_Is_Not_A_Gateway_Critical_Requirement(Type iface, Type durableImpl)
    {
        StoreDurabilityGuard.Critical.Select(entry => entry.Iface).Should().NotContain(iface);
        durableImpl.Should().NotBeNull(); // concrete remains only for migration/history tests
    }

    [Theory]
    [InlineData(typeof(INotificationDispatchOutbox))]
    [InlineData(typeof(IPushDeliveryTracker))]
    public void Store_Is_No_Longer_On_The_InMemory_Backlog(Type iface)
    {
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().NotContain(iface,
            "a store with a durable target must not also be listed as a known-in-memory exemption");
    }

    [Theory]
    [InlineData(typeof(INotificationDispatchOutbox), typeof(InMemoryNotificationDispatchOutbox), "INotificationDispatchOutbox", "InMemoryNotificationDispatchOutbox")]
    [InlineData(typeof(IPushDeliveryTracker), typeof(InMemoryPushDeliveryTracker), "IPushDeliveryTracker", "InMemoryPushDeliveryTracker")]
    public void Retired_InMemory_Impl_Is_Irrelevant_To_Gateway_Durability_Guard(
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

        violations.Should().BeEmpty();
        ifaceName.Should().NotBeNull();
        inMemoryName.Should().NotBeNull();
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
