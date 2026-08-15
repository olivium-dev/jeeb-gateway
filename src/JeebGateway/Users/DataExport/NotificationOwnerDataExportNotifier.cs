using JeebGateway.Notifications;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Sends one idempotent ready command to the notification owner. Retry of the
/// leased export work reuses the same notification id and capability token.
/// </summary>
public sealed class NotificationOwnerDataExportNotifier(INotificationOwnerClient owner)
    : IDataExportNotifier
{
    public async Task NotifyReadyAsync(
        string userId,
        string exportId,
        string downloadToken,
        DateTimeOffset linkExpiresAt,
        CancellationToken ct)
    {
        var notificationId = NotificationOwnerEventId.FromIdempotencyKey(
            $"data-export-ready:{exportId}");
        await owner.PublishAsync(
            new NotificationOwnerEvent(
                notificationId,
                userId,
                "Your Jeeb data export is ready",
                "Your private export is ready to download.",
                "data_export_ready",
                new Dictionary<string, object?>
                {
                    ["export_id"] = exportId,
                    ["download_path"] = $"/users/me/data-export/{downloadToken}/download",
                    ["expires_at"] = linkExpiresAt
                }),
            ct);
    }
}
