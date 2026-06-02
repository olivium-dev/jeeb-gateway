using JeebGateway.DTOs.Feedback;

namespace JeebGateway.Services
{
    /// <summary>
    /// Orchestrates technician review data: grouped comments, technician from catalog, and person profiles per commenter.
    /// </summary>
    public interface ITechnicianReviewService
    {
        /// <summary>
        /// Gets technician review: grouped comments enriched with person data and technician (catalog) data.
        /// </summary>
        /// <param name="tag">Tag identifier (used for feedback and as catalog item id).</param>
        /// <param name="length">Page size for grouped comments.</param>
        /// <param name="offset">Pagination offset.</param>
        /// <param name="filter">Filter: 0=all, 1=positive, 2=negative.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<TechnicianReviewResponseDto> GetTechnicianReviewAsync(
            string tag,
            int length,
            int offset,
            int filter,
            CancellationToken cancellationToken = default);
    }
}
