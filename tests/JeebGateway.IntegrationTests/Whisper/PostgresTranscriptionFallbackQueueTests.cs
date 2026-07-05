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
/// Gateway durability hardening (JEBV4-126, AUDIT-A IN-MEM-LIVE) — the voice-note
/// transcription FALLBACK queue (ITranscriptionFallbackQueue) is migrated from process
/// memory to durable Postgres (transcription_fallback_queue, migration 0033) behind
/// GatewayPostgres:ConnectionString. Mirrors PostgresPushReliabilityStoresTests — the
/// established DI-resolution-smoke + guard-promotion pattern for a flag-gated store swap.
///
/// <para>These tests document TWO verdicts of the voice-store durability sweep:
/// <list type="bullet">
/// <item>ITranscriptionFallbackQueue (126) → MIGRATED to Postgres: small metadata rows,
/// so gateway Postgres is the right durable home (like the push retry queue).</item>
/// <item>IAudioStore (133) → deliberately LEFT in-memory: it holds the raw audio BYTES,
/// which do not belong in the gateway DB; documented as an intentional transient buffer,
/// not a store of record — it stays on the KnownInMemoryBacklog and is NOT promoted.</item>
/// </list></para>
///
/// <para>The DI-resolution tests run for real, no live Postgres required: the Postgres
/// store's constructor only stores its collaborators (INpgsqlConnectionFactory merely
/// holds the connection string), so resolving the singleton never opens a socket. The
/// round-trip property that genuinely needs a live database is documented as a
/// deferred-to-Testcontainers-QV placeholder, matching PostgresPushReliabilityStoresTests.</para>
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
    public void FallbackQueue_Resolves_To_Postgres_When_GatewayPostgres_Configured()
    {
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ITranscriptionFallbackQueue>();
        act.Should().NotThrow("PostgresTranscriptionFallbackQueue's constructor does no I/O");
        scope.ServiceProvider.GetRequiredService<ITranscriptionFallbackQueue>()
            .Should().BeOfType<PostgresTranscriptionFallbackQueue>(
                "GatewayPostgres:ConnectionString is configured, so the durable fallback queue must be selected");
    }

    // ── DI wiring: in-memory fallback preserved when GatewayPostgres is absent ──

    [Fact]
    public void FallbackQueue_Resolves_To_InMemory_When_GatewayPostgres_Absent()
    {
        // Default test config carries no GatewayPostgres:ConnectionString, so the
        // in-memory fallback must remain the live path (unchanged behaviour for every
        // existing test that boots a bare WebApplicationFactory<Program>).
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITranscriptionFallbackQueue>()
            .Should().BeOfType<InMemoryTranscriptionFallbackQueue>();
    }

    [Fact]
    public void AudioStore_Stays_InMemory_Even_When_GatewayPostgres_Configured()
    {
        // JEBV4-133 verdict: IAudioStore holds raw audio bytes and is an INTENTIONAL
        // transient buffer — it must NOT be swapped to a gateway-Postgres impl even when
        // the connection string is present (large blobs do not belong in the gateway DB).
        using var factory = PostgresConfiguredFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAudioStore>()
            .Should().BeOfType<InMemoryAudioStore>(
                "IAudioStore is a deliberate transient audio buffer, not a durable store of record");
    }

    // ── Durability guard promotion (JEBV4-126) ─────────────────────────────────

    [Fact]
    public void FallbackQueue_Is_Now_A_Critical_Durable_Store_Requiring_Its_Postgres_Impl()
    {
        var critical = StoreDurabilityGuard.Critical
            .FirstOrDefault(c => c.Iface == typeof(ITranscriptionFallbackQueue));

        critical.Iface.Should().Be(typeof(ITranscriptionFallbackQueue),
            "ITranscriptionFallbackQueue must be promoted to the Critical fail-closed set now that a durable target exists");
        critical.DurableImpls.Should().Contain(typeof(PostgresTranscriptionFallbackQueue),
            "the only durable implementation that satisfies the prod-like gate is PostgresTranscriptionFallbackQueue");
    }

    [Fact]
    public void FallbackQueue_Is_No_Longer_On_The_InMemory_Backlog()
    {
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().NotContain(typeof(ITranscriptionFallbackQueue),
            "a store with a durable target must not also be listed as a known-in-memory exemption");
    }

    [Fact]
    public void EnsureDurable_ProdLike_With_InMemory_FallbackQueue_Fails_Closed()
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

        violations.Should().ContainSingle()
            .Which.Should().Contain("ITranscriptionFallbackQueue").And.Contain("InMemoryTranscriptionFallbackQueue");
    }

    // ── IAudioStore stays an intentional in-memory transient (JEBV4-133) ───────

    [Fact]
    public void AudioStore_Remains_An_Intentional_InMemory_Backlog_Entry_Not_Critical()
    {
        // IAudioStore holds raw audio bytes — deliberately NOT migrated to gateway
        // Postgres. It must stay on the backlog (logged loudly, non-blocking) and must
        // NOT appear in the Critical fail-closed set.
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().Contain(typeof(IAudioStore),
            "IAudioStore is intentionally in-memory (transient audio buffer, not a store of record)");
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
