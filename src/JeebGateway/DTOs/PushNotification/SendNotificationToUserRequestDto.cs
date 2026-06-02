using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.PushNotification
{
    /// <summary>
    /// Request payload for sending a notification to a specific user
    /// </summary>
    public class SendNotificationToUserRequestDto
    {
        /// <summary>
        /// Notification payload
        /// </summary>
        [Required(ErrorMessage = "Payload is required")]
        public object Payload { get; set; } = new object();
    }
}

