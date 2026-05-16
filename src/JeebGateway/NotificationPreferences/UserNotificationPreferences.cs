namespace JeebGateway.NotificationPreferences;

public class UserNotificationPreferences
{
    public required string UserId { get; init; }
    public bool Offers { get; set; }
    public bool Chat { get; set; }
    public bool StatusChanges { get; set; }
    public bool RatingReminders { get; set; }
    public bool Promotions { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
