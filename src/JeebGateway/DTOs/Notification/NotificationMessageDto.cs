using System;

namespace JeebGateway.DTOs.Notification
{
    /// <summary>
    /// Response payload for a single notification message
    /// </summary>
    public class NotificationMessageDto
    {
        /// <summary>
        /// Notification ID identifier
        /// </summary>
        public string NotificationId { get; set; } = string.Empty;

        /// <summary>
        /// Notification title
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Notification subtitle
        /// </summary>
        public string Subtitle { get; set; } = string.Empty;

        /// <summary>
        /// Notification description
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Type of notification
        /// </summary>
        public string NotificationType { get; set; } = string.Empty;

        /// <summary>
        /// Whether the notification has been read
        /// </summary>
        public bool IsRead { get; set; }

        /// <summary>
        /// Whether the notification is deactivated
        /// </summary>
        public bool Deactivated { get; set; }

        /// <summary>
        /// Creation timestamp
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; }
    }
}




