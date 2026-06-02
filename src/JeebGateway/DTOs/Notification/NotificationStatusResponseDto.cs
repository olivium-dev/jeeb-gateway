namespace JeebGateway.DTOs.Notification
{
    /// <summary>
    /// Response payload for notification status operations
    /// </summary>
    public class NotificationStatusResponseDto
    {
        /// <summary>
        /// Whether the operation was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Status message
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}




