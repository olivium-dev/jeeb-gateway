using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.PushNotification
{
    /// <summary>
    /// Request payload for broadcasting a notification to all users
    /// </summary>
    public class BroadcastNotificationRequestDto
    {
        /// <summary>
        /// Notification payload
        /// </summary>
        [Required(ErrorMessage = "Payload is required")]
        public object Payload { get; set; } = new object();
    }
}

