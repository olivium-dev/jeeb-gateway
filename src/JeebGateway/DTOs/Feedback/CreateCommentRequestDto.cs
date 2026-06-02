using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.Feedback
{
    /// <summary>
    /// Request payload for creating a comment/review
    /// </summary>
    public class CreateCommentRequestDto
    {
        /// <summary>
        /// Rating (1-5)
        /// </summary>
        [Required(ErrorMessage = "Rating is required")]
        [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5")]
        public int Rating { get; set; }

        /// <summary>
        /// Tag identifier
        /// </summary>
        [Required(ErrorMessage = "Tag is required")]
        [StringLength(100, MinimumLength = 1, ErrorMessage = "Tag must be between 1 and 100 characters")]
        public string Tag { get; set; } = string.Empty;

        /// <summary>
        /// Criteria identifier
        /// </summary>
        [Required(ErrorMessage = "Criteria is required")]
        [StringLength(50, MinimumLength = 1, ErrorMessage = "Criteria must be between 1 and 50 characters")]
        public string Criteria { get; set; } = string.Empty;

        /// <summary>
        /// Comment text
        /// </summary>
        [StringLength(1000, ErrorMessage = "Text must not exceed 1000 characters")]
        public string? Text { get; set; }

        /// <summary>
        /// Review title
        /// </summary>
        [StringLength(200, ErrorMessage = "ReviewTitle must not exceed 200 characters")]
        public string? ReviewTitle { get; set; }

        /// <summary>
        /// Media items
        /// </summary>
        public List<MediaItemDto>? Media { get; set; }
    }
}

