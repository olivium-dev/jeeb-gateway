using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.Notification
{
    /// <summary>
    /// Request payload for bulk marking notifications read/unread
    /// </summary>
    public class BulkMarkNotificationsRequestDto
    {
        /// <summary>
        /// List of notification IDs to update
        /// </summary>
        [Required(ErrorMessage = "NotificationIds is required")]
        public List<string> NotificationIds { get; set; } = new();
    }
}




