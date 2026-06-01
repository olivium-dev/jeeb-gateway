using System.Collections.Generic;

namespace JeebGateway.DTOs.Notification
{
    /// <summary>
    /// Response payload for paginated notification messages
    /// </summary>
    public class PagedNotificationMessagesResponseDto
    {
        /// <summary>
        /// Current page number (starts from 1)
        /// </summary>
        public int Page { get; set; }

        /// <summary>
        /// Number of items per page
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Total count of items across all pages
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// List of notification messages for the current page
        /// </summary>
        public List<NotificationMessageDto> Items { get; set; } = new();
    }
}




