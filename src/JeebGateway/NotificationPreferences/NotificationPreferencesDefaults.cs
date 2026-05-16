namespace JeebGateway.NotificationPreferences;

public static class NotificationPreferencesDefaults
{
    public static readonly IReadOnlyList<string> AlwaysOnChannels = new[]
    {
        "otp",
        "system_critical"
    };

    public static UserNotificationPreferences NewDefault(string userId) => new()
    {
        UserId = userId,
        Offers = true,
        Chat = true,
        StatusChanges = true,
        RatingReminders = true,
        Promotions = true,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
