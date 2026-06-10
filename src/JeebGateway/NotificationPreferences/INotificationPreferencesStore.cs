namespace JeebGateway.NotificationPreferences;

/// <summary>
/// Storage abstraction for per-user notification toggles. Production uses
/// <see cref="RemoteUserPreferencesNotificationPreferencesStore"/> backed by the
/// Rust remote-user-preferences service (JEB-1498).
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
    public bool? Settlements { get; init; }
}
