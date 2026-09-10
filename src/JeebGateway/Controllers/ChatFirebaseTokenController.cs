using System.Security.Claims;
using System.Text.Json.Serialization;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Chat.Firebase;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>Authenticated BFF exchange for chat-service-owned Firebase identity.
/// The gateway derives the opaque uid from the caller's validated claims and
/// proxies only; it has no Firebase signing material or local mint fallback.
/// Firebase membership/visibility rules, not token expiry, control live access.
/// A Firebase custom token grants project-wide identity, so all Firebase product
/// rules must be restrictive. Jeeb logout does not revoke a Firebase session.
/// </summary>
[ApiController]
public sealed class ChatFirebaseTokenController : ControllerBase
{
    private readonly IChatFirebaseIdentityClient _identity;
    private readonly ILogger<ChatFirebaseTokenController> _logger;

    public ChatFirebaseTokenController(
        IChatFirebaseIdentityClient identity,
        ILogger<ChatFirebaseTokenController> logger)
    {
        _identity = identity;
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
    // W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
    [HttpPost("chat/firebase-token")]
    [Authorize]
    [RequireCapability(Capabilities.ChatRead)] // ADR-005 §F {client,jeeber}; membership = STATE (Firestore rules)
    [ProducesResponseType(typeof(FirebaseTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> MintFirebaseToken(CancellationToken ct)
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

        // SEC — the uid is SERVER-DERIVED from the validated bearer's claims and nothing
        // else. UserIdentity's last arm falls back to the raw X-User-Id request header
        // whenever EdgeIdentityTrust.HeadersTrusted is true, which is UNCONDITIONAL in
        // Development and Testing. For every other endpoint that arm is merely a
        // convenience; here it would be privilege escalation — a client-supplied header
        // would mint a FIREBASE IDENTITY for an arbitrary user, and the Firestore
        // membership rule would then faithfully admit it to that user's conversations.
        // Re-deriving the claim chain and demanding an exact match closes the header path
        // by CONSTRUCTION, in every environment, rather than by trusting that production
        // never turns edge trust on. This is a no-op for real bearers: gateway-minted
        // sessions always carry `sub`, UM-reissued ones carry `sid`.
        var claimUid = User.FindFirstValue(ClaimTypes.Sid)
                       ?? User.FindFirstValue("sid")
                       ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(claimUid) || !string.Equals(claimUid, userId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refusing to mint a Firebase token for an identity that is not claim-derived.");

            return Unauthorized();
        }

        Response.Headers.CacheControl = "private, no-store";
        try
        {
            var minted = await _identity.MintAsync(userId, ct);
            // Never return another actor's identity or a partial/expired grant.
            if (!string.Equals(minted.Uid, userId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(minted.Token)
                || minted.ExpiresAt <= DateTime.UtcNow
                || minted.ExpiresAt > DateTime.UtcNow.AddHours(1).AddMinutes(1)
                || minted.ExpiresInSeconds is <= 0 or > 3600)
            {
                throw new InvalidOperationException("Chat owner identity binding was invalid.");
            }
            return Ok(minted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
            or System.Text.Json.JsonException or OperationCanceledException)
        {
            // Deliberately omit upstream bodies/exceptions: they may contain tokens.
            _logger.LogWarning("Chat owner Firebase identity unavailable ({ErrorType}).",
                ex.GetType().Name);
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
