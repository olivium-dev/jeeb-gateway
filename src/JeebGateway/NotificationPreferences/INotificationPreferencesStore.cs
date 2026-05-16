namespace JeebGateway.NotificationPreferences;

/// <summary>
/// Storage abstraction for per-user notification toggles. The default in-memory
/// implementation is intended for early-MVP local runs; production wiring will
/// proxy to the notification-service backing store via an NSwag-generated client.
/// </summary>
public interface INotificationPreferencesStore
{
    Task<UserNotificationPreferences> GetAsync(string userId, CancellationToken ct);

    Task<UserNotificationPreferences> UpdateAsync(
        string userId,
        NotificationPreferencesPatch patch,
        CancellationToken ct);
}

public class NotificationPreferencesPatch
{
    public bool? Offers { get; init; }
    public bool? Chat { get; init; }
    public bool? StatusChanges { get; init; }
    public bool? RatingReminders { get; init; }
    public bool? Promotions { get; init; }
}
