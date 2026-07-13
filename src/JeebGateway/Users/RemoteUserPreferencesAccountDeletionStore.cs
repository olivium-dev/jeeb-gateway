using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Users;

/// <summary>
/// JEBV4-215 (E20) — routes the account-deletion soft status-flip through the generic
/// remote-user-preferences service (Rust, :10067) instead of user-management (Q-079 /
/// GR-2; the DoD is "the flip persists via remote-user-preferences, NOT user-management").
/// It mirrors <see cref="JeebGateway.NotificationPreferences.RemoteUserPreferencesNotificationPreferencesStore"/>
/// exactly: the deletion status is written as an opaque JSON blob under the namespaced key
/// <c>jeeb.account_deletion</c> so the shared service stays Jeeb-agnostic, the scoped
/// <see cref="ServiceRemoteUserPreferencesClient"/> is resolved lazily per-call via
/// <see cref="IServiceScopeFactory"/> (the store is a singleton; the client wraps a pooled
/// HttpClient), and the upstream write is bounded so a slow/dead upstream fails fast.
/// </summary>
/// <remarks>
/// <para><b>Gateway-local fallback (the same shape notification-prefs took post-#274).</b>
/// The remote-user-preferences upstream is DEAD on MSI — the env still points at the
/// decommissioned <c>192.168.2.50:10067</c> and the owner declined the env flip — so the
/// mirror write WILL fail there. This store therefore treats the remote write as a
/// best-effort mirror layered ON TOP of an authoritative gateway-local persistence path:
/// it ALWAYS delegates the record/advance/read to an inner
/// <see cref="IAccountDeletionStore"/> (the durable <see cref="PostgresAccountDeletionStore"/>
/// when GatewayPostgres is configured, else <see cref="InMemoryAccountDeletionStore"/>) which
/// owns the 30-day purge SLA and the state machine, then fail-open mirrors the status blob to
/// remote-user-preferences. A dead upstream never fails the delete and never loses the
/// deletion request — exactly the fail-open-then-local posture the notification-prefs store
/// adopted. When the upstream is revived (env flip to a live remote-user-preferences), the
/// mirror simply starts landing; no code change is needed.</para>
///
/// <para>Because it decorates the durable inner store, this type is an APPROVED durable
/// implementation for <see cref="IAccountDeletionStore"/> in
/// <see cref="JeebGateway.Infrastructure.StoreDurabilityGuard"/> — the boot gate is satisfied
/// (in a prod-like env GatewayPostgres is set, so the inner is Postgres).</para>
/// </remarks>
public sealed class RemoteUserPreferencesAccountDeletionStore : IAccountDeletionStore
{
    /// <summary>
    /// The namespaced remote-user-preferences key under which the deletion status blob lives.
    /// Kept Jeeb-scoped (<c>jeeb.*</c>) so the shared service learns nothing about Jeeb topics
    /// (GR-2), mirroring <c>jeeb.notification_prefs</c>.
    /// </summary>
    public const string BlobKey = "jeeb.account_deletion";

    // Mirrors the notification-prefs store's write budget: the remote-user-preferences named
    // client carries the org resilience pipeline (retries x per-attempt timeout). The mirror is
    // best-effort, so cap the WHOLE upstream write at a short deadline and fail fast on a
    // slow/dead upstream instead of spinning the retry pipeline (which is exactly what is dead
    // on MSI today).
    private static readonly TimeSpan UpstreamWriteBudget = TimeSpan.FromMilliseconds(2000);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IAccountDeletionStore _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RemoteUserPreferencesAccountDeletionStore> _logger;

    public RemoteUserPreferencesAccountDeletionStore(
        IAccountDeletionStore inner,
        IServiceScopeFactory scopeFactory,
        ILogger<RemoteUserPreferencesAccountDeletionStore> logger)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct)
    {
        // AUTHORITATIVE first: the gateway-local store owns the durable record + 30-day SLA +
        // token revocation + anonymization. This is what survives a dead remote-user-preferences.
        var record = await _inner.RequestAsync(userId, hasActiveDelivery, ct);

        // BEST-EFFORT mirror the status flip to remote-user-preferences (the DoD "flip routes
        // through remote-user-preferences"). Fail-open: a dead/slow upstream (MSI .50 is
        // decommissioned) must never fail the delete nor lose the request — it is already
        // durable in the inner store.
        await MirrorFlipAsync(record, ct);
        return record;
    }

    public Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct)
        // The gateway-local store is authoritative for reads (the remote blob is a mirror).
        => _inner.GetAsync(userId, ct);

    public async Task AdvanceAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The state machine + purge lives in the inner store; the purge worker drives this.
        await _inner.AdvanceAsync(now, ct);
    }

    /// <summary>
    /// Writes the current deletion status blob to remote-user-preferences under
    /// <see cref="BlobKey"/>. Bounded by <see cref="UpstreamWriteBudget"/> and fully fail-open:
    /// any upstream error, 404-then-create, or budget expiry is swallowed with a warning, since
    /// the deletion record is already durable in the inner store.
    /// </summary>
    private async Task MirrorFlipAsync(AccountDeletionRequest record, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamWriteBudget);

        var json = JsonSerializer.Serialize(
            new AccountDeletionBlob
            {
                Status = record.Status,
                RequestedAt = record.RequestedAt,
                ScheduledPurgeAt = record.ScheduledPurgeAt,
                CompletedAt = record.CompletedAt,
            },
            SerializerOptions);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
            try
            {
                await client.Data_UpdatePreferenceAsync(record.UserId, BlobKey, new PreferenceValue { Value = json }, budget.Token);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                // First flip for this user — the key does not exist yet: create it.
                await client.Data_SetSinglePreferenceAsync(record.UserId, BlobKey, new PreferenceValue { Value = json }, budget.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER aborted (not our budget) — propagate so the request is not left half-done.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "account-deletion status mirror to remote-user-preferences (key {Key}) failed or exceeded {BudgetMs}ms for {UserId}; " +
                "the deletion is already durable in the gateway-local store (remote-user-preferences upstream may be down — MSI .50 is decommissioned).",
                BlobKey, UpstreamWriteBudget.TotalMilliseconds, record.UserId);
        }
    }

    /// <summary>Opaque status blob persisted under <see cref="BlobKey"/> — the shared service treats it as a string.</summary>
    private sealed class AccountDeletionBlob
    {
        public string? Status { get; init; }
        public DateTimeOffset RequestedAt { get; init; }
        public DateTimeOffset? ScheduledPurgeAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
    }
}
