using System.Text.Json;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using Microsoft.Extensions.Logging;

namespace JeebGateway.NotificationPreferences;

/// <summary>
/// Production store for per-user notification preferences backed by the generic
/// remote-user-preferences service (Rust, :10067). Preferences are stored as an
/// opaque JSON blob under the namespaced key <c>jeeb.notification_prefs</c> so the
/// shared service remains Jeeb-agnostic (GR2 / JEB-1498).
/// Fail-open: any upstream error on GET falls back to defaults.
/// </summary>
public sealed class RemoteUserPreferencesNotificationPreferencesStore : INotificationPreferencesStore
{
    private const string BlobKey = "jeeb.notification_prefs";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly ServiceRemoteUserPreferencesClient _client;
    private readonly ILogger<RemoteUserPreferencesNotificationPreferencesStore> _logger;

    public RemoteUserPreferencesNotificationPreferencesStore(
        ServiceRemoteUserPreferencesClient client,
        ILogger<RemoteUserPreferencesNotificationPreferencesStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<UserNotificationPreferences> GetAsync(string userId, CancellationToken ct)
    {
        try
        {
            var pref = await _client.Data_GetSinglePreferenceAsync(userId, BlobKey, ct);
            if (pref?.Value is { } json)
            {
                var blob = JsonSerializer.Deserialize<NotificationPreferencesBlob>(json, SerializerOptions);
                if (blob is not null)
                    return FromBlob(userId, blob);
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // First access: no prefs stored yet — fall through to defaults.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read notification preferences for user {UserId}; using defaults (fail-open)", userId);
        }
        return NotificationPreferencesDefaults.NewDefault(userId);
    }

    public async Task<UserNotificationPreferences> UpdateAsync(
        string userId,
        NotificationPreferencesPatch patch,
        CancellationToken ct)
    {
        var current = await GetAsync(userId, ct);
        ApplyPatch(current, patch);
        var json = JsonSerializer.Serialize(ToBlob(current), SerializerOptions);
        try
        {
            await _client.Data_UpdatePreferenceAsync(userId, BlobKey, new PreferenceValue { Value = json }, ct);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            await _client.Data_SetSinglePreferenceAsync(userId, BlobKey, new PreferenceValue { Value = json }, ct);
        }
        return current;
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
