using System.Text.Json.Serialization;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Chat.Firebase;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// The identity hop that lets the mobile client read its own chat thread directly
/// from Firestore instead of re-fetching it over REST.
///
/// <para>The client already holds a Jeeb session. Firestore does not understand Jeeb
/// sessions — it understands Firebase identities. This route exchanges the one for
/// the other: authenticate with the existing Jeeb JWT, receive a short-lived Firebase
/// CUSTOM token whose <c>uid</c> is the caller's Jeeb user id. The client feeds that
/// to <c>signInWithCustomToken</c>, and the Firestore rules deployed to
/// <c>jeeb-5a293</c> admit it to exactly the conversations whose
/// <c>Participants[].UserId</c> contains that uid, and to nothing else.</para>
///
/// <para>This route mints an identity ASSERTION only. It grants no authority of its
/// own: every read it enables is still adjudicated by the Firestore ruleset against
/// conversation membership. It never widens an existing Jeeb permission, and it is
/// unreachable without a valid Jeeb session.</para>
/// </summary>
[ApiController]
public sealed class ChatFirebaseTokenController : ControllerBase
{
    private readonly IFirebaseCustomTokenMinter _minter;
    private readonly ILogger<ChatFirebaseTokenController> _logger;

    public ChatFirebaseTokenController(
        IFirebaseCustomTokenMinter minter,
        ILogger<ChatFirebaseTokenController> logger)
    {
        _minter = minter;
        _logger = logger;
    }

    /// <summary>
    /// Mint a Firebase custom token for the authenticated caller.
    /// </summary>
    /// <remarks>
    /// ADR-005: capability-marked <see cref="Capabilities.ChatRead"/>. The token's only
    /// purpose is reading the caller's own chat, so it carries the same coarse CLAIM the
    /// REST chat-read endpoints carry ({client, jeeber}); WHICH conversations it can
    /// actually open remains STATE, enforced by the Firestore membership rule — the same
    /// division of labour <see cref="JeebConversationsController"/> already uses.
    /// </remarks>
    [HttpPost("v1/chat/firebase-token")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE (Firestore rules)
    [ProducesResponseType(typeof(FirebaseTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult MintFirebaseToken()
    {
        // The SAME resolver every other chat endpoint uses (sid → sub → trusted-edge
        // header). This is the whole correctness argument for the route: chat-service
        // stamps this exact value into Conversation.Participants[].UserId, so minting
        // the uid from it makes `request.auth.uid == Participants[].UserId` true by
        // construction rather than by assumption.
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        try
        {
            var minted = _minter.Mint(userId);

            return Ok(new FirebaseTokenResponse
            {
                Token = minted.Token,
                Uid = minted.Uid,
                ExpiresAt = minted.ExpiresAt.UtcDateTime,
                ExpiresInSeconds = (int)Math.Max(
                    0, Math.Round((minted.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)),
            });
        }
        catch (FirebaseCustomTokenUnavailableException ex)
        {
            // Misconfiguration is an operator problem, not a client problem: 503, and the
            // reason is logged rather than returned so a probe cannot map the host's
            // credential layout.
            _logger.LogError("Firebase custom-token mint unavailable: {Reason}", ex.Message);

            return Problem(
                title: "Firebase chat token minting is not available.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "https://jeeb.dev/errors/firebase-token-unavailable");
        }
        catch (ArgumentException)
        {
            // The resolved Jeeb user id is not usable as a Firebase uid (empty, or over
            // the 128-byte limit). Nothing the caller can fix by retrying.
            _logger.LogError("Resolved Jeeb user id is not a valid Firebase uid.");

            return Problem(
                title: "Firebase chat token minting is not available.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "https://jeeb.dev/errors/firebase-token-unavailable");
        }
    }
}

/// <summary>
/// Mint response. snake_case wire, pinned per-field, matching the rest of the chat BFF.
/// </summary>
public sealed class FirebaseTokenResponse
{
    /// <summary>The Firebase custom token to hand to <c>signInWithCustomToken</c>.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The Jeeb user id embedded as the token's uid — the caller's own id.</summary>
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    /// <summary>Absolute expiry (UTC).</summary>
    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>Seconds until expiry, so the client can schedule a refresh.</summary>
    [JsonPropertyName("expires_in_seconds")]
    public int ExpiresInSeconds { get; set; }
}
