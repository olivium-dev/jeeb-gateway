using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.PushNotification
{
    /// <summary>
    /// Request payload for deleting a device registration
    /// </summary>
    public class DeleteDeviceRequestDto
    {
        /// <summary>
        /// Device ID identifier
        /// </summary>
        [Required(ErrorMessage = "DeviceId is required")]
        public string DeviceId { get; set; } = string.Empty;
    }
}

