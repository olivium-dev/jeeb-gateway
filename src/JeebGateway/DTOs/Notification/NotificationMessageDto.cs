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
        /// Optional primary entity id (e.g. deliveryId, offerId) carried by the upstream
        /// payload and used to build the <see cref="DeepLink"/>. Empty when not applicable.
        /// </summary>
        public string EntityId { get; set; } = string.Empty;

        /// <summary>
        /// NOT-02 — gateway-resolved client deep-link for this row (e.g.
        /// <c>jeeb://deliveries/{id}/tracking</c>). Always populated; falls back to the
        /// inbox root for types with no specific destination.
        /// </summary>
        public string DeepLink { get; set; } = string.Empty;

        /// <summary>
        /// Creation timestamp
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; }
    }
}




