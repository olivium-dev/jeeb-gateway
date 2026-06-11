using System.Collections.Concurrent;

namespace JeebGateway.NotificationPreferences;

public class InMemoryNotificationPreferencesStore : INotificationPreferencesStore
{
    private readonly ConcurrentDictionary<string, UserNotificationPreferences> _store = new();

    public Task<UserNotificationPreferences> GetAsync(string userId, CancellationToken ct)
    {
        var prefs = _store.GetOrAdd(userId, NotificationPreferencesDefaults.NewDefault);
        return Task.FromResult(Clone(prefs));
    }

    public Task<UserNotificationPreferences> UpdateAsync(
        string userId,
        NotificationPreferencesPatch patch,
        CancellationToken ct)
    {
        var updated = _store.AddOrUpdate(
            userId,
            _ => Apply(NotificationPreferencesDefaults.NewDefault(userId), patch),
            (_, existing) => Apply(existing, patch));

        return Task.FromResult(Clone(updated));
    }

    private static UserNotificationPreferences Apply(UserNotificationPreferences prefs, NotificationPreferencesPatch patch)
    {
        if (patch.Offers is { } offers) prefs.Offers = offers;
        if (patch.Chat is { } chat) prefs.Chat = chat;
        if (patch.StatusChanges is { } status) prefs.StatusChanges = status;
        if (patch.RatingReminders is { } rating) prefs.RatingReminders = rating;
        if (patch.Promotions is { } promotions) prefs.Promotions = promotions;
        if (patch.Settlements is { } settlements) prefs.Settlements = settlements;
        prefs.UpdatedAt = DateTimeOffset.UtcNow;
        return prefs;
    }

    private static UserNotificationPreferences Clone(UserNotificationPreferences prefs) => new()
    {
        UserId = prefs.UserId,
        Offers = prefs.Offers,
        Chat = prefs.Chat,
        StatusChanges = prefs.StatusChanges,
        RatingReminders = prefs.RatingReminders,
        Promotions = prefs.Promotions,
        Settlements = prefs.Settlements,
        UpdatedAt = prefs.UpdatedAt
    };
}
