namespace JeebGateway.DTOs.Notification
{
    /// <summary>
    /// NOT-02 — unread bell-badge count for the authenticated user.
    /// </summary>
    public class UnreadNotificationCountResponseDto
    {
        /// <summary>
        /// Number of unread notifications for the current user. Capped at
        /// <see cref="Capped"/>=true when the true count exceeds the badge ceiling.
        /// </summary>
        public int UnreadCount { get; set; }

        /// <summary>
        /// True when <see cref="UnreadCount"/> is the display ceiling (e.g. "99+")
        /// rather than the exact total. Lets the client render "99+" without guessing.
        /// </summary>
        public bool Capped { get; set; }
    }
}
