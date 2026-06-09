using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace JeebGateway.Conversations.Client;

/// <summary>
/// Raised when chat-service answers a conversation call with a non-success
/// status. Carries the upstream status + body so the BFF controller can forward
/// it VERBATIM (RFC 7807 on error) instead of collapsing every upstream fault to
/// a generic 500 — the org gateway contract.
/// </summary>
public sealed class JeebConversationApiException : Exception
{
    public JeebConversationApiException(HttpStatusCode statusCode, string? body)
        : base($"chat-service conversation call failed with {(int)statusCode}.")
    {
        StatusCode = statusCode;
        Body = body;
    }

    public HttpStatusCode StatusCode { get; }
    public string? Body { get; }
}

/// <summary>
/// HttpClient-backed <see cref="IJeebConversationClient"/>. Hand-authored against
/// chat-service's Jeeb conversation aggregate contract (see
/// <c>JeebConversationContracts</c> for the why), mirroring the
/// <c>BanServiceClient</c> / <c>NotificationServiceClient</c> hand-coded
/// precedent. The named HttpClient injected here supplies BaseAddress + the
/// org-standard bearer / X-Service-Auth forwarding + resilience pipeline, so this
/// class never thinks about retry/timeout/auth — it only marshals JSON and maps
/// the upstream status onto <see cref="JeebConversationApiException"/> so the BFF
/// can forward it verbatim.
///
/// Newtonsoft serializer (repo-wide for chat clients); snake_case wire is pinned
/// per-field on the DTOs.
/// </summary>
public sealed class JeebConversationClient : IJeebConversationClient
{
    private readonly HttpClient _http;

    public JeebConversationClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<JeebConversationResponse> CreateConversationAsync(
        CreateJeebConversationRequest request, CancellationToken ct)
    {
        // chat-service: POST /api/conversations  { request_id, client_user_id, idempotency_key }
        using var msg = new HttpRequestMessage(HttpMethod.Post, "api/conversations")
        {
            Content = JsonContent(request),
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }
        return await SendAsync<JeebConversationResponse>(msg, ct);
    }

    public async Task<JeebConversationResponse> GetConversationByCorrelationAsync(
        string correlationKey, CancellationToken ct)
    {
        // chat-service: GET /api/conversations?correlationKey={key}
        var url = $"api/conversations?correlationKey={Uri.EscapeDataString(correlationKey)}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<JeebConversationResponse>(msg, ct);
    }

    public async Task<JeebMessageResponse> AppendMessageAsync(
        string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
    {
        // chat-service: POST /api/conversations/{id}/messages  { kind, subtype, audience, body|payload, author_id }
        var url = $"api/conversations/{Uri.EscapeDataString(conversationId)}/messages";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent(request),
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }
        return await SendAsync<JeebMessageResponse>(msg, ct);
    }

    public async Task<JeebMessageListResponse> ListMessagesForViewerAsync(
        string conversationId, string viewerUserId, CancellationToken ct)
    {
        // chat-service: GET /api/conversations/{id}/messages?viewer={viewerUserId}
        // chat-service owns the VisibilityFilter and returns only the viewer's slice.
        var url = $"api/conversations/{Uri.EscapeDataString(conversationId)}/messages"
                + $"?viewer={Uri.EscapeDataString(viewerUserId)}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<JeebMessageListResponse>(msg, ct);
    }

    public async Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
        string conversationId, string viewerUserId, string cursor, CancellationToken ct)
    {
        // chat-service: GET /api/conversations/{id}/messages/since/{cursor}?viewer={viewerUserId}
        // chat-service owns the VisibilityFilter and returns only the viewer's
        // post-cursor slice — IDENTICAL filtering to the full read (A6 parity).
        var url = $"api/conversations/{Uri.EscapeDataString(conversationId)}"
                + $"/messages/since/{Uri.EscapeDataString(cursor)}"
                + $"?viewer={Uri.EscapeDataString(viewerUserId)}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<JeebMessageListResponse>(msg, ct);
    }

    public async Task<JeebConversationMembership> GetMembershipAsync(
        string conversationId, string viewerUserId, CancellationToken ct)
    {
        // chat-service: GET /api/conversations/{id}/membership?viewer={viewerUserId}
        var url = $"api/conversations/{Uri.EscapeDataString(conversationId)}/membership"
                + $"?viewer={Uri.EscapeDataString(viewerUserId)}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync<JeebConversationMembership>(msg, ct);
    }

    public async Task<JeebConversationParticipant> AddParticipantAsync(
        string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
    {
        // chat-service: POST /api/conversations/{id}/participants  { user_id, role_in_convo }
        var url = $"api/conversations/{Uri.EscapeDataString(conversationId)}/participants";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent(request),
        };
        return await SendAsync<JeebConversationParticipant>(msg, ct);
    }

    public async Task<JeebConversationResponse> AdvancePhaseAsync(
        string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
    {
        // chat-service: PATCH /api/conversations/{id}/phase  { phase, winner_user_id, winner_role_in_convo, remove_others }
        var url = $"api/conversations/{Uri.EscapeDataString(conversationId)}/phase";
        using var msg = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent(request),
        };
        return await SendAsync<JeebConversationResponse>(msg, ct);
    }

    // -----------------------------------------------------------------
    // transport
    // -----------------------------------------------------------------

    private static HttpContent JsonContent<T>(T body) =>
        new StringContent(
            JsonConvert.SerializeObject(body),
            Encoding.UTF8,
            "application/json");

    private async Task<T> SendAsync<T>(HttpRequestMessage msg, CancellationToken ct)
        where T : class
    {
        using var response = await _http.SendAsync(msg, ct);
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Forward the upstream status + body verbatim to the BFF controller.
            throw new JeebConversationApiException(response.StatusCode, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new JeebConversationApiException(
                HttpStatusCode.BadGateway,
                $"chat-service {msg.RequestUri} returned an empty body.");
        }

        var parsed = JsonConvert.DeserializeObject<T>(body);
        if (parsed is null)
        {
            throw new JeebConversationApiException(
                HttpStatusCode.BadGateway,
                $"chat-service {msg.RequestUri} returned an unparseable body.");
        }

        return parsed;
    }
}
