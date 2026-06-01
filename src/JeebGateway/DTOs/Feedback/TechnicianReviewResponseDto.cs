using JeebGateway.service.ServiceCatalog;
using JeebGateway.service.ServiceFeedback;

namespace JeebGateway.DTOs.Feedback
{
    /// <summary>
    /// Response for technician review: grouped comments with person data per review, plus technician data from catalog
    /// </summary>
    public class TechnicianReviewResponseDto
    {
        /// <summary>
        /// Total number of reviews
        /// </summary>
        public int TotalReviewCount { get; set; }

        /// <summary>
        /// Average rating across all reviews
        /// </summary>
        public double AverageRating { get; set; }

        /// <summary>
        /// Grouped comments (one per commenter) with person/profile data for each
        /// </summary>
        public List<GroupedCommentWithPersonDto> GroupedComments { get; set; } = null!;

        /// <summary>
        /// Technician/item data from the catalog (when tag is a valid catalog item id)
        /// </summary>
        public ItemResponse? Technician { get; set; }
    }
}
