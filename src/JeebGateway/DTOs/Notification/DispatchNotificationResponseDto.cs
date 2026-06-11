namespace JeebGateway.DTOs.Notification;

/// <summary>
/// Response body for <c>POST /api/notifications</c>.
/// </summary>
public sealed class DispatchNotificationResponseDto
{
    public Guid EntryId { get; set; }
    public bool WasDeduplicated { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}
