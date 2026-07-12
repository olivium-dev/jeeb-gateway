using System.Text.Json;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JeebGateway.NotificationPreferences;

/// <summary>
/// Production store for per-user notification preferences backed by the generic
/// remote-user-preferences service (Rust, :10067). Preferences are stored as an
/// opaque JSON blob under the namespaced key <c>jeeb.notification_prefs</c> so the
/// shared service remains Jeeb-agnostic (GR2 / JEB-1498).
/// Fail-open: any upstream error on GET falls back to defaults.
/// </summary>
/// <remarks>
/// This store is registered as a singleton (it is consumed transitively by other
/// singletons such as <c>PushAutoOfflineNotifier</c> via the push pipeline), but the
/// underlying <see cref="ServiceRemoteUserPreferencesClient"/> is scoped (it wraps a
/// pooled <c>IHttpClientFactory</c> client). To avoid a captive dependency we resolve
/// the scoped client lazily inside a per-call <see cref="IServiceScope"/> via
/// <see cref="IServiceScopeFactory"/> rather than constructor-injecting it. This keeps
/// the gateway booting cleanly under DI scope validation (Development) while preserving
/// the correct HttpClient lifetime.
/// </remarks>
public sealed class RemoteUserPreferencesNotificationPreferencesStore : INotificationPreferencesStore
{
    private const string BlobKey = "jeeb.notification_prefs";

    // JEBV4-30 (AC#4, gateway latency): the remote-user-preferences named client
    // (:10067) carries the org-standard resilience pipeline (3 retries x 10s
    // per-attempt timeout, 30s HttpClient cap). On a slow/erroring upstream that
    // let a fail-open READ burn ~13s and a read-modify-write PATCH exceed the
    // mobile client's 15s write timeout (toggle reverts, nothing persists).
    // These per-call budgets cap the WHOLE store operation so GET/PATCH complete
    // well under 2s on a healthy upstream and fail fast on a slow one instead of
    // spinning the retry pipeline. Notification preferences are a low-stakes
    // fail-open surface, so a short deadline is the right trade.
    private static readonly TimeSpan UpstreamReadBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan UpstreamWriteBudget = TimeSpan.FromMilliseconds(2000);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RemoteUserPreferencesNotificationPreferencesStore> _logger;

    public RemoteUserPreferencesNotificationPreferencesStore(
        IServiceScopeFactory scopeFactory,
        ILogger<RemoteUserPreferencesNotificationPreferencesStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<UserNotificationPreferences> GetAsync(string userId, CancellationToken ct)
    {
        // Fail-open READ: on any upstream error OR our budget expiring, return
        // defaults FAST (<=UpstreamReadBudget) rather than spinning the retry
        // pipeline for up to ~30s.
        var (_, prefs) = await ReadInternalAsync(userId, ct);
        return prefs;
    }

    public async Task<UserNotificationPreferences> UpdateAsync(
        string userId,
        NotificationPreferencesPatch patch,
        CancellationToken ct)
    {
        // The remote store only supports whole-blob get/set, so PATCH is a
        // read-modify-write: a full upstream READ followed by a WRITE. Bound the
        // WHOLE operation with one deadline so a slow upstream fails fast instead
        // of two stacked retry-pipeline round-trips blowing the client's 15s write
        // timeout.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamWriteBudget);

        var (answered, current) = await ReadInternalAsync(userId, budget.Token);
        // If the pre-read did NOT genuinely come back from upstream, abort the
        // write: blindly persisting merged-defaults would clobber the user's real
        // stored preferences. Surface a fast timeout the controller maps to 504.
        if (!answered)
            throw new TimeoutException(
                "remote-user-preferences did not respond within the read budget; aborting the " +
                "notification-preferences write to avoid overwriting stored preferences with defaults.");

        ApplyPatch(current, patch);
        var json = JsonSerializer.Serialize(ToBlob(current), SerializerOptions);
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
        try
        {
            try
            {
                await client.Data_UpdatePreferenceAsync(userId, BlobKey, new PreferenceValue { Value = json }, budget.Token);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                await client.Data_SetSinglePreferenceAsync(userId, BlobKey, new PreferenceValue { Value = json }, budget.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our write budget expired (not a caller cancellation) — fail fast.
            throw new TimeoutException(
                "remote-user-preferences did not accept the notification-preferences write within the budget.");
        }
        return current;
    }

    /// <summary>
    /// Reads the preference blob under a bounded deadline. Returns
    /// <c>answered=true</c> when the upstream produced a definitive answer
    /// (a blob, an empty/absent blob, or a 404 "no prefs yet"), and
    /// <c>answered=false</c> when the call errored or exceeded
    /// <see cref="UpstreamReadBudget"/> — in which case callers still get
    /// safe defaults but know the value is NOT authoritative. A genuine caller
    /// cancellation is propagated.
    /// </summary>
    private async Task<(bool answered, UserNotificationPreferences prefs)> ReadInternalAsync(string userId, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(UpstreamReadBudget);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ServiceRemoteUserPreferencesClient>();
            var pref = await client.Data_GetSinglePreferenceAsync(userId, BlobKey, budget.Token);
            if (pref?.Value is { } json)
            {
                var blob = JsonSerializer.Deserialize<NotificationPreferencesBlob>(json, SerializerOptions);
                if (blob is not null)
                    return (true, FromBlob(userId, blob));
            }
            // 200 with an empty/unparseable body: treat as "no prefs yet".
            return (true, NotificationPreferencesDefaults.NewDefault(userId));
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // First access: no prefs stored yet — a definitive answer.
            return (true, NotificationPreferencesDefaults.NewDefault(userId));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER aborted (not our budget) — propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "notification-preferences read for {UserId} failed or exceeded {BudgetMs}ms; failing open to defaults",
                userId, UpstreamReadBudget.TotalMilliseconds);
            return (false, NotificationPreferencesDefaults.NewDefault(userId));
        }
    }

    private static void ApplyPatch(UserNotificationPreferences prefs, NotificationPreferencesPatch patch)
    {
        if (patch.Offers is { } offers) prefs.Offers = offers;
        if (patch.Chat is { } chat) prefs.Chat = chat;
        if (patch.StatusChanges is { } status) prefs.StatusChanges = status;
        if (patch.RatingReminders is { } rating) prefs.RatingReminders = rating;
        if (patch.Promotions is { } promotions) prefs.Promotions = promotions;
        if (patch.Settlements is { } settlements) prefs.Settlements = settlements;
        prefs.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static UserNotificationPreferences FromBlob(string userId, NotificationPreferencesBlob blob) => new()
    {
        UserId = userId,
        Offers = blob.Offers ?? true,
        Chat = blob.Chat ?? true,
        StatusChanges = blob.StatusChanges ?? true,
        RatingReminders = blob.RatingReminders ?? true,
        Promotions = blob.Promotions ?? true,
        Settlements = blob.Settlements ?? true,
        UpdatedAt = blob.UpdatedAt ?? DateTimeOffset.UtcNow
    };

    private static NotificationPreferencesBlob ToBlob(UserNotificationPreferences prefs) => new()
    {
        Offers = prefs.Offers,
        Chat = prefs.Chat,
        StatusChanges = prefs.StatusChanges,
        RatingReminders = prefs.RatingReminders,
        Promotions = prefs.Promotions,
        Settlements = prefs.Settlements,
        UpdatedAt = prefs.UpdatedAt
    };

    private sealed class NotificationPreferencesBlob
    {
        public bool? Offers { get; init; }
        public bool? Chat { get; init; }
        public bool? StatusChanges { get; init; }
        public bool? RatingReminders { get; init; }
        public bool? Promotions { get; init; }
        public bool? Settlements { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }
}
