using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Notifications;
using JeebGateway.Users;
using JeebGateway.service.ServiceChat;
using ChatApiException = JeebGateway.service.ServiceChat.ApiException;

namespace JeebGateway.Controllers
{
    /// <summary>
    /// Controller for managing chat channels, messages, and members
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ServiceChatClient _serviceChatClient;
        private readonly IChatMessagePushNotifier _chatPush;
        private readonly ILogger<ChatController> _logger;

        public ChatController(
            ServiceChatClient serviceChatClient,
            IChatMessagePushNotifier chatPush,
            ILogger<ChatController> logger)
        {
            _serviceChatClient = serviceChatClient;
            _chatPush = chatPush;
            _logger = logger;
        }

        private void ValidateService()
        {
            if (_serviceChatClient == null)
            {
                throw new ChatApiException("Error: ServiceChatClient is not initialized", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// JEBV4-242 — map a caught upstream chat-service <see cref="ChatApiException"/>
        /// to a sanitized RFC 7807 <c>application/problem+json</c> result. The upstream
        /// status is preserved (clamped to a valid 4xx/5xx; anything else becomes
        /// 502 Bad Gateway), but the upstream exception message / response body is
        /// NEVER echoed to the caller — it is logged server-side only.
        ///
        /// <para><b>Why.</b> Every catch previously did
        /// <c>return StatusCode(ex.StatusCode, ex.Message)</c>, and the NSwag
        /// <see cref="ChatApiException"/>.Message embeds up to 512 chars of the raw
        /// upstream response body — an information-disclosure leak across ~30 chat
        /// endpoints. This mirrors the JEBV4-63 UserController fix and the
        /// JeebConversationsController / AuthEmailFacadeController upstream-mapping
        /// idiom (private helper → <c>Problem(...)</c> with a generic client-safe
        /// title, full detail to the log only).</para>
        /// </summary>
        private ActionResult UpstreamProblem(ChatApiException ex)
        {
            var status = ex.StatusCode is >= 400 and < 600
                ? ex.StatusCode
                : StatusCodes.Status502BadGateway;

            // Full upstream detail (ex.Message carries the wrapped upstream body,
            // ex.Response the raw payload) goes to the server log only — never on the wire.
            _logger.LogWarning(ex,
                "Chat BFF: chat-service call failed on {Method} {Path} → {Status}.",
                Request.Method, Request.Path, status);

            return Problem(
                title: "The chat request could not be completed.",
                statusCode: status);
        }

        private ActionResult InvalidMessageProblem(string detail) => Problem(
            title: "Invalid chat message request.",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest);

        private static AddMessageRequest ToUpstreamRequest(
            string channelId,
            AddChatMessageRequest request) => new()
        {
            MemberId = request.MemberId!,
            MemberID = request.MemberId!,
            ChannelId = channelId,
            ChannelID = channelId,
            SessionId = request.SessionId!,
            SessionID = request.SessionId!,
            Text = request.Text!,
            Payload = request.Payload!,
            ParentId = request.ParentId!,
            ParentID = request.ParentId!,
        };

        #region Health

        /// <summary>
        /// Health check endpoint
        /// </summary>
        /// <returns>Health status</returns>
        /// <response code="200">Service is healthy</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("health")]
        [PublicEndpoint("Chat-service health passthrough — ADR-005 §A public.")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> Health()
        {
            try
            {
                ValidateService();
                await _serviceChatClient.HealthAsync();
                return Ok();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error checking health: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Health check endpoint (alternative)
        /// </summary>
        /// <returns>Health status</returns>
        /// <response code="200">Service is healthy</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("health2")]
        [PublicEndpoint("Chat-service health passthrough — ADR-005 §A public.")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> Health2()
        {
            try
            {
                ValidateService();
                await _serviceChatClient.Health2Async();
                return Ok();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error checking health: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Check endpoint
        /// </summary>
        /// <returns>Check status</returns>
        /// <response code="200">Check successful</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("check")]
        [PublicEndpoint("Chat-service check passthrough — ADR-005 §A public.")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> Check()
        {
            try
            {
                ValidateService();
                await _serviceChatClient.CheckAsync();
                return Ok();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error checking: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        #endregion

        #region Channels

        /// <summary>
        /// Create a new channel
        /// </summary>
        /// <param name="request">Channel creation request</param>
        /// <returns>Created channel identity</returns>
        /// <response code="201">Channel created successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> CreateChannel([FromBody] CreateChannelRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.ChannelsAsync(request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error creating channel: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Add members to a channel
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="request">Add members request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Members added successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Channel not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/members")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> AddChannelMembers(string channelId, [FromBody] AddChannelMembersRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MembersPOSTAsync(channelId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error adding channel members: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Deactivate a channel
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Channel deactivated successfully</response>
        /// <response code="404">Channel not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/deactivate")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> DeactivateChannel(string channelId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.DeactivateAsync(channelId);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error deactivating channel: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get channel summary
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="memberId">Member ID</param>
        /// <returns>Channel summary</returns>
        /// <response code="200">Channel summary retrieved successfully</response>
        /// <response code="404">Channel not found</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("channels/{channelId}/summary")]
        [Authorize]
        [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(ChannelSummaryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<ChannelSummaryResponse>> GetChannelSummary(string channelId, [FromQuery] string memberId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(memberId))
                {
                    throw new ChatApiException("Member ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.SummaryAsync(channelId, memberId);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error retrieving channel summary: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get channel statistics
        /// </summary>
        /// <returns>Channel statistics</returns>
        /// <response code="200">Statistics retrieved successfully</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("channels/statistics")]
        [Authorize]
        [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F: authed chat read (any-auth preserved; not narrowed to admin)
        [ProducesResponseType(typeof(ChannelStatisticsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<ChannelStatisticsResponse>> GetChannelStatistics()
        {
            try
            {
                ValidateService();
                var response = await _serviceChatClient.StatisticsAsync();
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error retrieving channel statistics: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Find common channels
        /// </summary>
        /// <param name="request">Find common channels request</param>
        /// <returns>Common channels response</returns>
        /// <response code="200">Common channels retrieved successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/common")]
        [Authorize]
        [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F: common-channels lookup (read), {client,jeeber}
        [ProducesResponseType(typeof(CommonChannelsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<CommonChannelsResponse>> FindCommonChannels([FromBody] FindCommonChannelsRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.CommonAsync(request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error finding common channels: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Firebase endpoint for channel
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <returns>Firebase response</returns>
        /// <response code="200">Firebase operation successful</response>
        /// <response code="404">Channel not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/firebase")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> Firebase(string channelId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                await _serviceChatClient.FirebaseAsync(channelId);
                return Ok();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error in Firebase operation: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        #endregion

        #region Messages

        /// <summary>
        /// Add a message to a channel
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="request">Add message request</param>
        /// <returns>Identity response</returns>
        /// <response code="201">Message added successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Channel not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> AddMessage(
            string channelId,
            [FromBody] AddChatMessageRequest request)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return InvalidMessageProblem("Channel ID is required.");
            }

            if (request is null)
            {
                return InvalidMessageProblem("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return InvalidMessageProblem("Message text is required.");
            }

            if (string.IsNullOrWhiteSpace(request.ChannelId))
            {
                return InvalidMessageProblem("Message channel ID is required.");
            }

            if (!string.Equals(channelId, request.ChannelId, StringComparison.Ordinal))
            {
                return InvalidMessageProblem("Message channel ID must match the route channel ID.");
            }

            try
            {
                ValidateService();
                var response = await _serviceChatClient.MessagesPOSTAsync(
                    channelId,
                    ToUpstreamRequest(channelId, request));

                // BUILD-CHAT-PUSH — notify the conversation's other party (the only missing
                // link for real A→B chat push). Best-effort/degrade-don't-fail; never affects
                // this 201. NOTE: the legacy channels surface keys on channelId; recipient
                // resolution succeeds only when channelId matches a delivery request's stamped
                // ConversationId (else it no-ops). The live Jeeb chat path is /v1/conversations.
                if (UserIdentity.TryGetUserId(HttpContext, out var authorId, out _))
                {
                    await _chatPush.NotifyNewMessageAsync(channelId, authorId, request?.Text, HttpContext.RequestAborted);
                }

                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error adding message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Edit a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="request">Edit message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message edited successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPut("channels/{channelId}/messages")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; ownership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> EditMessage(string channelId, [FromBody] EditMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MessagesPUTAsync(channelId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error editing message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get a message by ID
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <returns>Message response</returns>
        /// <response code="200">Message retrieved successfully</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("channels/{channelId}/messages/{messageId}")]
        [Authorize]
        [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<MessageResponse>> GetMessage(string channelId, string messageId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MessagesGETAsync(channelId, messageId);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error retrieving message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Delete a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Delete message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message deleted successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("channels/{channelId}/messages/{messageId}")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; ownership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> DeleteMessage(string channelId, string messageId, [FromBody] DeleteMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MessagesDELETEAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error deleting message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Moderate a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Moderate message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message moderated successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/moderate")]
        [Authorize]
        [RequireCapability(Capabilities.ChatModerate)] // ADR-005 §F OPEN-1: moderation baked {admin}
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> ModerateMessage(string channelId, string messageId, [FromBody] ModerateMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.ModerateAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error moderating message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Reply to a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Reply to message request</param>
        /// <returns>Identity response</returns>
        /// <response code="201">Reply added successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/reply")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> ReplyToMessage(string channelId, string messageId, [FromBody] ReplyToMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.ReplyAsync(channelId, messageId, request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error replying to message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Bind a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Bind message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message bound successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/bind")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> BindMessage(string channelId, string messageId, [FromBody] BindMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.BindAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error binding message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Mask a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Mask message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message masked successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/mask")]
        [Authorize]
        [RequireCapability(Capabilities.ChatModerate)] // ADR-005 §F OPEN-1: mask = moderation, baked {admin}
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> MaskMessage(string channelId, string messageId, [FromBody] MaskMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MaskAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error masking message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Unmask a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Unmask message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message unmasked successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/unmask")]
        [Authorize]
        [RequireCapability(Capabilities.ChatModerate)] // ADR-005 §F OPEN-1: unmask = moderation, baked {admin}
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> UnmaskMessage(string channelId, string messageId, [FromBody] MaskMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.UnmaskAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error unmasking message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Hide a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Hide message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message hidden successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/hide")]
        [Authorize]
        [RequireCapability(Capabilities.ChatModerate)] // ADR-005 §F OPEN-1: hide = moderation, baked {admin}
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> HideMessage(string channelId, string messageId, [FromBody] HideMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.HideAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error hiding message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Unhide a message
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Unhide message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message unhidden successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/unhide")]
        [Authorize]
        [RequireCapability(Capabilities.ChatModerate)] // ADR-005 §F OPEN-1: unhide = moderation, baked {admin}
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> UnhideMessage(string channelId, string messageId, [FromBody] HideMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.UnhideAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error unhiding message: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Mark message as delivered
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Mark message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message marked as delivered</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/delivered")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> MarkMessageDelivered(string channelId, string messageId, [FromBody] MarkMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.DeliveredAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error marking message as delivered: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Mark message as seen
        /// </summary>
        /// <param name="channelId">Channel ID</param>
        /// <param name="messageId">Message ID</param>
        /// <param name="request">Mark message request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Message marked as seen</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Message not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("channels/{channelId}/messages/{messageId}/seen")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> MarkMessageSeen(string channelId, string messageId, [FromBody] MarkMessageRequest request)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(channelId))
                {
                    throw new ChatApiException("Channel ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ChatApiException("Message ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.SeenAsync(channelId, messageId, request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error marking message as seen: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// Create a new member
        /// </summary>
        /// <param name="request">Create member request</param>
        /// <returns>Created member identity</returns>
        /// <response code="201">Member created successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("members")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; identity scoping = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> CreateMember([FromBody] CreateMemberRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MembersPOST2Async(request);
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error creating member: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Update a member
        /// </summary>
        /// <param name="request">Update member request</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Member updated successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="404">Member not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPut("members")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; identity scoping = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> UpdateMember([FromBody] UpdateMemberRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MembersPUTAsync(request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error updating member: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// List members with pagination
        /// </summary>
        /// <param name="pageSize">Page size (optional)</param>
        /// <param name="startAfterDocumentId">Start after document ID for pagination (optional)</param>
        /// <returns>Paged list of members</returns>
        /// <response code="200">Members retrieved successfully</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("members")]
        [Authorize]
        [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; scoping = STATE
        [ProducesResponseType(typeof(MemberResponsePagedList), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<MemberResponsePagedList>> ListMembers([FromQuery] int? pageSize = null, [FromQuery] string? startAfterDocumentId = null)
        {
            try
            {
                ValidateService();
                var response = await _serviceChatClient.MembersGETAsync(pageSize, startAfterDocumentId);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error listing members: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Get member by ID
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <returns>Member details</returns>
        /// <response code="200">Member retrieved successfully</response>
        /// <response code="404">Member not found</response>
        /// <response code="500">Internal server error</response>
        [HttpGet("members/{memberId}")]
        [Authorize]
        [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; scoping = STATE
        [ProducesResponseType(typeof(MemberResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<MemberResponse>> GetMember(string memberId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(memberId))
                {
                    throw new ChatApiException("Member ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.MembersGET2Async(memberId);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error retrieving member: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Deactivate a member
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <returns>Identity response</returns>
        /// <response code="200">Member deactivated successfully</response>
        /// <response code="404">Member not found</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("members/{memberId}/deactivate")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; identity scoping = STATE
        [ProducesResponseType(typeof(IdentityResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<IdentityResponse>> DeactivateMember(string memberId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(memberId))
                {
                    throw new ChatApiException("Member ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.Deactivate2Async(memberId);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error deactivating member: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Delete a member
        /// </summary>
        /// <param name="memberId">Member ID</param>
        /// <returns>No content</returns>
        /// <response code="204">Member deleted successfully</response>
        /// <response code="404">Member not found</response>
        /// <response code="500">Internal server error</response>
        [HttpDelete("members/{memberId}")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; identity scoping = STATE
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> DeleteMember(string memberId)
        {
            try
            {
                ValidateService();
                if (string.IsNullOrEmpty(memberId))
                {
                    throw new ChatApiException("Member ID is required", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                await _serviceChatClient.MembersDELETEAsync(memberId);
                return NoContent();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error deleting member: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        #endregion

        #region Sessions

        /// <summary>
        /// Validate streams
        /// </summary>
        /// <param name="request">Validate streams request</param>
        /// <returns>Validation response</returns>
        /// <response code="200">Streams validated successfully</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("sessions/validate-streams")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(typeof(ValidateStreamsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<ValidateStreamsResponse>> ValidateStreams([FromBody] ValidateStreamsRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                var response = await _serviceChatClient.ValidateStreamsAsync(request);
                return Ok(response);
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error validating streams: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Keep session alive
        /// </summary>
        /// <param name="request">Session keep alive request</param>
        /// <returns>No content</returns>
        /// <response code="204">Session kept alive</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("sessions/keep-alive")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> KeepAlive([FromBody] SessionKeepAliveRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                await _serviceChatClient.KeepAliveAsync(request);
                return NoContent();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error keeping session alive: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        /// <summary>
        /// Validate session
        /// </summary>
        /// <param name="request">Validate session request</param>
        /// <returns>No content</returns>
        /// <response code="204">Session validated</response>
        /// <response code="400">Bad request</response>
        /// <response code="500">Internal server error</response>
        [HttpPost("sessions/validate")]
        [Authorize]
        [RequireCapability(Capabilities.ChatSend)] // ADR-005 §F {client,jeeber}; membership = STATE
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> ValidateSession([FromBody] ValidateSessionRequest request)
        {
            try
            {
                ValidateService();
                if (request == null)
                {
                    throw new ChatApiException("Request body cannot be null", 400, "Bad Request", new Dictionary<string, IEnumerable<string>>(), null);
                }

                await _serviceChatClient.ValidateAsync(request);
                return NoContent();
            }
            catch (ChatApiException ex)
            {
                return UpstreamProblem(ex);
            }
            catch (Exception ex)
            {
                throw new ChatApiException($"Error validating session: {ex.Message}, Stack trace: {ex.StackTrace}", 500, "Internal Server Error", new Dictionary<string, IEnumerable<string>>(), null);
            }
        }

        #endregion
    }

    /// <summary>
    /// Canonical gateway input for POST /api/Chat/channels/{channelId}/messages.
    /// The generated chat-service DTO cannot be used for ASP.NET model binding because
    /// it contains case-colliding property pairs such as memberId/memberID.
    /// </summary>
    public sealed class AddChatMessageRequest
    {
        public string? MemberId { get; set; }
        public string? ChannelId { get; set; }
        public string? SessionId { get; set; }
        public string? Text { get; set; }
        public string? Payload { get; set; }
        public string? ParentId { get; set; }
    }
}
