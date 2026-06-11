namespace JeebGateway.NotificationPreferences;

public class UserNotificationPreferences
{
    public required string UserId { get; init; }
    public bool Offers { get; set; }
    public bool Chat { get; set; }
    public bool StatusChanges { get; set; }
    public bool RatingReminders { get; set; }
    public bool Promotions { get; set; }
    /// <summary>jeeb.settlement_paid notifications (JEB-1498 gap-S12).</summary>
    public bool Settlements { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
