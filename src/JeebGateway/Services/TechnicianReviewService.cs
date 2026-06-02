using Microsoft.Extensions.Logging;
using JeebGateway.DTOs.Feedback;
using JeebGateway.service.ServiceCatalog;
using JeebGateway.service.ServiceFeedback;
using JeebGateway.service.ServiceUserManagement;
using CatalogApiException = JeebGateway.service.ServiceCatalog.ApiException;
using UserManagementApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.Services
{
    /// <summary>
    /// Orchestrates technician review: grouped comments from feedback, technician from catalog, and person profiles per commenter.
    /// </summary>
    public sealed class TechnicianReviewService : ITechnicianReviewService
    {
        private readonly ServiceFeedbackClient _feedbackClient;
        private readonly ServiceCatalogClient _catalogClient;
        private readonly ServiceUserManagementClient _userClient;
        private readonly ILogger<TechnicianReviewService> _logger;

        public TechnicianReviewService(
            ServiceFeedbackClient feedbackClient,
            ServiceCatalogClient catalogClient,
            ServiceUserManagementClient userClient,
            ILogger<TechnicianReviewService> logger)
        {
            _feedbackClient = feedbackClient ?? throw new ArgumentNullException(nameof(feedbackClient));
            _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
            _userClient = userClient ?? throw new ArgumentNullException(nameof(userClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<TechnicianReviewResponseDto> GetTechnicianReviewAsync(
            string tag,
            int length,
            int offset,
            int filter,
            CancellationToken cancellationToken = default)
        {
            var groupedResponse = await _feedbackClient.GroupedAsync(tag, length, offset, filter, cancellationToken);

            var technicianTask = TryGetTechnicianByTagAsync(tag, cancellationToken).AsTask();
            var enrichedCommentsTask = EnrichWithPersonProfilesAsync(groupedResponse.GroupedComments, cancellationToken);

            await Task.WhenAll(technicianTask, enrichedCommentsTask);

            var technician = await technicianTask;
            var enrichedComments = await enrichedCommentsTask;
            return BuildResponse(groupedResponse, enrichedComments, technician);
        }

        /// <summary>
        /// Fetches catalog item (technician) by tag when tag is a valid GUID.
        /// </summary>
        private async ValueTask<ItemResponse?> TryGetTechnicianByTagAsync(string tag, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(tag) || !Guid.TryParse(tag, out var tagGuid))
                return null;

            try
            {
                return await _catalogClient.ItemGETAsync(tagGuid, cancellationToken);
            }
            catch (CatalogApiException ex)
            {
                _logger.LogDebug(ex, "Catalog item not found for tag {Tag}", tag);
                return null;
            }
        }

        /// <summary>
        /// Enriches each grouped comment with the commenter's profile from user service (parallel calls).
        /// </summary>
        private async Task<IReadOnlyList<GroupedCommentWithPersonDto>> EnrichWithPersonProfilesAsync(
            ICollection<GroupedCommentResponse>? groupedComments,
            CancellationToken cancellationToken)
        {
            if (groupedComments == null || groupedComments.Count == 0)
                return Array.Empty<GroupedCommentWithPersonDto>();

            var tasks = groupedComments.Select(g => EnrichSingleCommentAsync(g, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// Fetches person profile for one commenter and returns review + person.
        /// </summary>
        private async Task<GroupedCommentWithPersonDto> EnrichSingleCommentAsync(
            GroupedCommentResponse review,
            CancellationToken cancellationToken)
        {
            UserProfileResponse? person = null;
            try
            {
                person = await _userClient.ProfileAsync(review.CommenterId.ToString(), cancellationToken);
            }
            catch (UserManagementApiException ex)
            {
                _logger.LogDebug(ex, "Profile not found for commenter {CommenterId}", review.CommenterId);
            }

            return new GroupedCommentWithPersonDto { Review = review, Person = person };
        }

        /// <summary>
        /// Builds the final DTO from grouped response, enriched comments, and optional technician.
        /// </summary>
        private static TechnicianReviewResponseDto BuildResponse(
            GetGroupedCommentsResponse groupedResponse,
            IReadOnlyList<GroupedCommentWithPersonDto> enrichedComments,
            ItemResponse? technician)
        {
            return new TechnicianReviewResponseDto
            {
                TotalReviewCount = groupedResponse.TotalReviewCount,
                AverageRating = groupedResponse.AverageRating,
                GroupedComments = enrichedComments.ToList(),
                Technician = technician
            };
        }
    }
}
