using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.DTOs.Feedback;
using JeebGateway.Services;
using JeebGateway.service.ServiceFeedback;
using FeedbackApiException = JeebGateway.service.ServiceFeedback.ApiException;

namespace JeebGateway.Controllers
{
    /// <summary>
    /// Controller for managing feedback and comments
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class FeedbackController : ControllerBase
    {
        private readonly ServiceFeedbackClient _serviceFeedbackClient;
        private readonly ITechnicianReviewService _technicianReviewService;
        private readonly ILogger<FeedbackController> _logger;

        public FeedbackController(
            ServiceFeedbackClient serviceFeedbackClient,
            ITechnicianReviewService technicianReviewService,
            ILogger<FeedbackController> logger)
        {
            _serviceFeedbackClient = serviceFeedbackClient;
            _technicianReviewService = technicianReviewService;
            _logger = logger;
        }

        private ActionResult<(Guid userId, bool isValid)> ValidateUserAndServices()
        {
            var userIdString = User.FindFirst(ClaimTypes.Sid)?.Value;
            if (string.IsNullOrEmpty(userIdString))
            {
                userIdString = User.FindFirst("sid")?.Value;
            }
            if (string.IsNullOrEmpty(userIdString))
            {
                userIdString = User.FindFirst("sub")?.Value;
            }
            
            if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            {
                throw new FeedbackApiException("Unauthorized: User ID not found in token or invalid format", 401, "Unauthorized", new Dictionary<string, IEnumerable<string>>(), null);
            }

            if (_serviceFeedbackClient == null)
            {
                throw new FeedbackApiException("Error: ServiceFeedbackClient is not initialized", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }

            return (userId, true);
        }

        /// <summary>
        /// Create a new comment/review
        /// </summary>
        /// <remarks>
        /// Create a new comment or review. The commenter ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <param name="request">Comment creation request</param>
        /// <returns>Created comment</returns>
        /// <response code="200">Comment created successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="409">Conflict</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("comment")]
        [Authorize]
        [ProducesResponseType(typeof(CommentResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<CommentResponse>> CreateComment([FromBody] CreateCommentRequestDto request)
        {
            try
            {
                if (request == null)
                {
                    throw new FeedbackApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var commenterId = validationResult.Value.userId;

                var serviceRequest = new CreateCommentRequest
                {
                    CommenterId = commenterId,
                    Rating = request.Rating,
                    Tag = request.Tag,
                    Criteria = request.Criteria,
                    Text = request.Text,
                    ReviewTitle = request.ReviewTitle,
                    Media = request.Media?.Select(m => new MediaItem
                    {
                        MediaPath = m.MediaPath,
                        MediaMime = m.MediaMime
                    }).ToList()
                };

                var response = await _serviceFeedbackClient.CommentPOSTAsync(serviceRequest);
                return Ok(response);
            }
            catch (FeedbackApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new FeedbackApiException($"Error creating comment: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get comments
        /// </summary>
        /// <remarks>
        /// Get comments with pagination and filtering
        /// </remarks>
        /// <param name="tag">Tag identifier</param>
        /// <param name="length">Number of items to return</param>
        /// <param name="offset">Offset for pagination</param>
        /// <param name="filter">Filter type (0=all, 1=positive, 2=negative)</param>
        /// <returns>List of comments</returns>
        /// <response code="200">Comments retrieved successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("comment")]
        [ProducesResponseType(typeof(GetCommentsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<GetCommentsResponse>> GetComments(
            [FromQuery] string tag,
            [FromQuery] int length = 10,
            [FromQuery] int offset = 0,
            [FromQuery] int filter = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(tag))
                {
                    throw new FeedbackApiException("Tag parameter is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceFeedbackClient.CommentGETAsync(tag, length, offset, filter);
                return Ok(response);
            }
            catch (FeedbackApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new FeedbackApiException($"Error retrieving comments: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Delete a comment
        /// </summary>
        /// <remarks>
        /// Delete a comment. The commenter ID is automatically extracted from the Bearer token.
        /// </remarks>
        /// <param name="request">Comment deletion request</param>
        /// <returns>Number of deleted comments</returns>
        /// <response code="200">Comment deleted successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="401">Unauthorized</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("comment")]
        [Authorize]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<int>> DeleteComment([FromBody] DeleteCommentRequestDto request)
        {
            try
            {
                if (request == null)
                {
                    throw new FeedbackApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var validationResult = ValidateUserAndServices();
                if (validationResult.Result != null)
                {
                    return validationResult.Result;
                }

                var commenterId = validationResult.Value.userId;
                var serviceRequest = new DeleteCommentRequest
                {
                    CommentId = request.CommentId,
                    CommenterId = commenterId,
                    Tag = request.Tag,
                    Criteria = request.Criteria
                };

                var response = await _serviceFeedbackClient.CommentDELETEAsync(serviceRequest);
                return Ok(response);
            }
            catch (FeedbackApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new FeedbackApiException($"Error deleting comment: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get grouped comments
        /// </summary>
        /// <remarks>
        /// Get comments grouped by commenter with pagination and filtering
        /// </remarks>
        /// <param name="tag">Tag identifier</param>
        /// <param name="length">Number of items to return</param>
        /// <param name="offset">Offset for pagination</param>
        /// <param name="filter">Filter type (0=all, 1=positive, 2=negative)</param>
        /// <returns>Grouped comments</returns>
        /// <response code="200">Grouped comments retrieved successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("grouped")]
        [ProducesResponseType(typeof(GetGroupedCommentsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<GetGroupedCommentsResponse>> GetGroupedComments(
            [FromQuery] string tag,
            [FromQuery] int length = 10,
            [FromQuery] int offset = 0,
            [FromQuery] int filter = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(tag))
                {
                    throw new FeedbackApiException("Tag parameter is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceFeedbackClient.GroupedAsync(tag, length, offset, filter);
                return Ok(response);
            }
            catch (FeedbackApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new FeedbackApiException($"Error retrieving grouped comments: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get technician review (grouped comments) with technician data from catalog and person data per commenter
        /// </summary>
        /// <remarks>
        /// Returns comments grouped by commenter, each enriched with commenter profile, plus technician/item data from catalog (tag = catalog id).
        /// </remarks>
        /// <param name="tag">Tag identifier (used as catalog item id for technician data)</param>
        /// <param name="length">Number of items to return</param>
        /// <param name="offset">Offset for pagination</param>
        /// <param name="filter">Filter type (0=all, 1=positive, 2=negative)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Grouped comments with person data and technician data</returns>
        /// <response code="200">Technician review retrieved successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("technician-review")]
        [ProducesResponseType(typeof(TechnicianReviewResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<TechnicianReviewResponseDto>> GetTechnicanReview(
            [FromQuery] string tag,
            [FromQuery] int length = 10,
            [FromQuery] int offset = 0,
            [FromQuery] int filter = 0,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(tag))
                {
                    throw new FeedbackApiException("Tag parameter is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _technicianReviewService.GetTechnicianReviewAsync(tag, length, offset, filter, cancellationToken);
                return Ok(response);
            }
            catch (FeedbackApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new FeedbackApiException($"Error retrieving technician review: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get average rating
        /// </summary>
        /// <remarks>
        /// Get the average rating for a specific tag
        /// </remarks>
        /// <param name="tag">Tag identifier</param>
        /// <returns>Average rating</returns>
        /// <response code="200">Average rating retrieved successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("rating")]
        [ProducesResponseType(typeof(GetAverageRatingResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<GetAverageRatingResponse>> GetAverageRating([FromQuery] string tag)
        {
            try
            {
                if (string.IsNullOrEmpty(tag))
                {
                    throw new FeedbackApiException("Tag parameter is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceFeedbackClient.RatingAsync(tag);
                return Ok(response);
            }
            catch (FeedbackApiException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                throw new FeedbackApiException($"Error retrieving average rating: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }
    }
}

