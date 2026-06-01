using JeebGateway.service.ServiceFeedback;
using JeebGateway.service.ServiceUserManagement;

namespace JeebGateway.DTOs.Feedback
{
    /// <summary>
    /// A grouped comment (review per person) with that person's profile data
    /// </summary>
    public class GroupedCommentWithPersonDto
    {
        /// <summary>
        /// The grouped review/comment from the feedback service
        /// </summary>
        public GroupedCommentResponse Review { get; set; } = null!;

        /// <summary>
        /// Person/commenter profile from user service (when commenter id resolves)
        /// </summary>
        public UserProfileResponse? Person { get; set; }
    }
}
