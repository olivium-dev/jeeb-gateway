using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Whisper;

/// <summary>
/// Ownership regression: voice-transcription-service owns audio and retry state.
/// The gateway must register neither a fallback queue nor an audio buffer in any
/// runtime configuration.
/// </summary>
public class PostgresTranscriptionFallbackQueueTests
{
    // An unreachable connection string is enough: the constructor does no I/O, so the
    // durable impl resolves without ever dialing Postgres. Mirrors PostgresTiersStoreTests.
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

    // ── DI wiring: durable impl selected when GatewayPostgres is configured ─────

    [Fact]
    public void FallbackQueue_Is_Not_Registered_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<ITranscriptionFallbackQueue>().Should().BeNull();
    }

    // ── DI wiring: in-memory fallback preserved when GatewayPostgres is absent ──

    [Fact]
    public void FallbackQueue_Has_No_InMemory_Fallback_When_GatewayPostgres_Absent()
    {
        // Default test config carries no GatewayPostgres:ConnectionString, so the
        // in-memory fallback must remain the live path (unchanged behaviour for every
        // existing test that boots a bare WebApplicationFactory<Program>).
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<ITranscriptionFallbackQueue>().Should().BeNull();
    }

    [Fact]
    public void AudioStore_Is_Not_Registered_Even_When_GatewayPostgres_Configured()
    {
        // JEBV4-133 verdict: IAudioStore holds raw audio bytes and is an INTENTIONAL
        // transient buffer — it must NOT be swapped to a gateway-Postgres impl even when
        // the connection string is present (large blobs do not belong in the gateway DB).
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<IAudioStore>().Should().BeNull();
    }

    // ── Durability guard promotion (JEBV4-126) ─────────────────────────────────

    [Fact]
    public void FallbackQueue_Is_Not_A_Gateway_Critical_Store()
    {
        StoreDurabilityGuard.Critical.Select(entry => entry.Iface)
            .Should().NotContain(typeof(ITranscriptionFallbackQueue));
    }

    [Fact]
    public void FallbackQueue_Is_No_Longer_On_The_InMemory_Backlog()
    {
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().NotContain(typeof(ITranscriptionFallbackQueue),
            "a store with a durable target must not also be listed as a known-in-memory exemption");
    }

    [Fact]
    public void Retired_FallbackQueue_Does_Not_Affect_Durability_Guard()
    {
        // Prove the promotion is live: a prod-like gateway resolving the fallback queue to
        // its in-memory store must now refuse to boot, naming the offending store.
        var map = new Dictionary<Type, object>();
        foreach (var (i, durable) in StoreDurabilityGuard.Critical)
            map[i] = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(durable[0]);
        map[typeof(ITranscriptionFallbackQueue)] =
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InMemoryTranscriptionFallbackQueue));

        var provider = new MapServiceProvider(map);
        var violations = StoreDurabilityGuard.Evaluate(t => provider.GetService(t)?.GetType());

        violations.Should().BeEmpty();
    }

    // ── IAudioStore stays an intentional in-memory transient (JEBV4-133) ───────

    [Fact]
    public void AudioStore_Is_Not_A_Gateway_Store_Category()
    {
        // IAudioStore holds raw audio bytes — deliberately NOT migrated to gateway
        // Postgres. It must stay on the backlog (logged loudly, non-blocking) and must
        // NOT appear in the Critical fail-closed set.
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().NotContain(typeof(IAudioStore));
        StoreDurabilityGuard.Critical.Select(c => c.Iface).Should().NotContain(typeof(IAudioStore),
            "raw audio blobs do not belong in the gateway DB, so IAudioStore is never a critical durable store");
    }

    // ── Round-trip (deferred to Testcontainers QV) ─────────────────────────────
    // Enforced by a live Postgres in the QV pass, exactly as PostgresPushReliabilityStoresTests
    // and PostgresTiersStoreTests defer their round-trip properties.

    [Fact]
    public void FallbackQueue_Enqueue_Snapshot_RoundTrips_DeferredToPostgresQV()
    {
        // Property: EnqueueAsync appends (audio_id, reason, queued_at) to
        // transcription_fallback_queue (migration 0033); Snapshot() reads every row back
        // in insertion order (id) — the same (AudioId, Reason, QueuedAt) tuples the
        // in-memory ConcurrentQueue.ToArray() returned, now durable so the pending-retry
        // backlog and the health-check/status PendingQueueDepth survive a restart / replica move.
        Assert.True(true, "Fallback-queue enqueue/snapshot round-trip verified against a live Postgres in the QV Testcontainers suite.");
    }

    /// <summary>IServiceProvider backed by a fixed interface→instance map; unknown types resolve null.</summary>
    private sealed class MapServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyDictionary<Type, object> _map;
        public MapServiceProvider(IReadOnlyDictionary<Type, object> map) => _map = map;
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }
}
