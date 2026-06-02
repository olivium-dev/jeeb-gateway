using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.Feedback
{
    /// <summary>
    /// Media item DTO
    /// </summary>
    public class MediaItemDto
    {
        /// <summary>
        /// Media path
        /// </summary>
        [Required(ErrorMessage = "MediaPath is required")]
        [StringLength(500, MinimumLength = 1, ErrorMessage = "MediaPath must be between 1 and 500 characters")]
        public string MediaPath { get; set; } = string.Empty;

        /// <summary>
        /// Media MIME type
        /// </summary>
        [Required(ErrorMessage = "MediaMime is required")]
        [StringLength(100, MinimumLength = 1, ErrorMessage = "MediaMime must be between 1 and 100 characters")]
        public string MediaMime { get; set; } = string.Empty;
    }
}

