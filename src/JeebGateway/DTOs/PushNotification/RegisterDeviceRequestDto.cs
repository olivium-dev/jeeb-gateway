using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.PushNotification
{
    /// <summary>
    /// Request payload for registering a device for push notifications
    /// </summary>
    public class RegisterDeviceRequestDto
    {
        /// <summary>
        /// FCM (Firebase Cloud Messaging) token
        /// </summary>
        [Required(ErrorMessage = "FcmToken is required")]
        public string FcmToken { get; set; } = string.Empty;

        /// <summary>
        /// Device ID identifier
        /// </summary>
        [Required(ErrorMessage = "DeviceId is required")]
        public string DeviceId { get; set; } = string.Empty;
    }
}

