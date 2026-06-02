using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.Feedback
{
    /// <summary>
    /// Request payload for deleting a comment
    /// </summary>
    public class DeleteCommentRequestDto
    {
        /// <summary>
        /// Comment ID
        /// </summary>
        [Required(ErrorMessage = "CommentId is required")]
        public Guid CommentId { get; set; }

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
    }
}

