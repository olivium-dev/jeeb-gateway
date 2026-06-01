using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IOfferServiceClient"/>.
/// Hand-coded against the verified routes on
/// <c>offer-service/lib/offer_service_web/router.ex</c> (the service exposes no
/// OpenAPI document, so there is no NSwag client to generate). Mirrors the
/// <c>NotificationServiceClient</c> hand-coded precedent: an explicit
/// snake_case naming policy plus per-field <see cref="JsonPropertyNameAttribute"/>
/// on the wire DTOs, and 404 → typed "not found" rather than an exception.
///
/// The typed client (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) carries the
/// org-standard resilience pipeline and the BaseAddress
/// (<c>Services:Offer:BaseUrl</c>), so this class never thinks about
/// retry / timeout / circuit-breaker.
///
/// Auth: offer-service trusts the gateway-injected <c>x-user-id</c> header
/// (its <c>AuthenticatedUser</c> plug). Each call sets that header from the
/// acting user id rather than relying on bearer forwarding.
/// </summary>
public sealed class OfferServiceClient : IOfferServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string UserIdHeader = "x-user-id";
    private const string IdempotencyHeader = "Idempotency-Key";

    private readonly HttpClient _http;

    public OfferServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<OfferWire> SubmitAsync(
        string actingUserId,
        string requestId,
        long feeCents,
        int etaMinutes,
        string? note,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/v1/requests/{Uri.EscapeDataString(requestId)}/offers");
        SetUser(request, actingUserId);
        request.Content = JsonContent.Create(
            new SubmitBody { FeeCents = feeCents, EtaMinutes = etaMinutes, Note = note },
            options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);

        // Translate the upstream's typed conflict codes back into the same
        // exceptions the in-memory store throws, so RequestOffersController's
        // existing catch blocks (DuplicateOfferException /
        // TooManyOffersForRequestException) keep working unchanged.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var code = await ReadErrorCodeAsync(response, ct);
            throw new OfferUpstreamConflictException(requestId, code);
        }

        response.EnsureSuccessStatusCode();
        return await DeserializeOfferAsync(response, ct);
    }

    public async Task<OfferWithdrawResult> WithdrawAsync(
        string actingUserId,
        string requestId,
        string offerId,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/v1/requests/{Uri.EscapeDataString(requestId)}/offers/{Uri.EscapeDataString(offerId)}");
        SetUser(request, actingUserId);

        using var response = await _http.SendAsync(request, ct);

        return response.StatusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.NoContent => OfferWithdrawResult.Withdrawn,
            HttpStatusCode.NotFound => OfferWithdrawResult.NotFound,
            HttpStatusCode.Forbidden => OfferWithdrawResult.NotOwned,
            // 409 (not pending) and 410 (already withdrawn → accept returns 410)
            // both mean "no longer retractable".
            HttpStatusCode.Conflict or HttpStatusCode.Gone => OfferWithdrawResult.NotPending,
            _ => ThrowUnexpected(response)
        };
    }

    public async Task<OfferAcceptWire> AcceptAsync(
        string actingUserId,
        string requestId,
        string offerId,
        string idempotencyKey,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/requests/{Uri.EscapeDataString(requestId)}/offers/{Uri.EscapeDataString(offerId)}/accept");
        SetUser(request, actingUserId);
        request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        // Empty JSON body — accept takes no required payload (confirm_high_fee
        // is optional and defaults false).
        request.Content = JsonContent.Create(new { }, options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<AcceptEnvelope>(JsonOptions, ct)
            ?? throw new HttpRequestException(
                $"offer-service {response.RequestMessage?.RequestUri} returned an empty accept body.");

        var replayed = response.Headers.TryGetValues("x-idempotency-replay", out var values)
                       && values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

        return new OfferAcceptWire
        {
            AcceptedOfferId = wire.AcceptedOffer?.Id ?? offerId,
            ChatThreadId = wire.ChatThreadId,
            OtpCode = wire.OtpCode,
            RejectedOfferIds = wire.RejectedOfferIds ?? new List<string>(),
            Replayed = replayed,
        };
    }

    private static void SetUser(HttpRequestMessage request, string actingUserId)
        => request.Headers.TryAddWithoutValidation(UserIdHeader, actingUserId);

    private static async Task<OfferWire> DeserializeOfferAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var wire = await response.Content.ReadFromJsonAsync<WireOffer>(JsonOptions, ct);
        if (wire is null)
        {
            throw new HttpRequestException(
                $"offer-service {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return new OfferWire
        {
            Id = wire.Id ?? string.Empty,
            RequestId = wire.RequestId ?? string.Empty,
            JeeberId = wire.JeeberId ?? string.Empty,
            FeeCents = wire.FeeCents,
            EtaMinutes = wire.EtaMinutes,
            Note = wire.Note,
            Status = wire.Status ?? string.Empty,
            EditsCount = wire.EditsCount,
            CreatedAt = wire.CreatedAt,
            UpdatedAt = wire.UpdatedAt,
            WithdrawnAt = wire.WithdrawnAt,
        };
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var env = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(JsonOptions, ct);
            return env?.Error?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OfferWithdrawResult ThrowUnexpected(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode(); // throws with the upstream status
        return OfferWithdrawResult.NotFound; // unreachable
    }

    // --- wire DTOs (snake_case as emitted by offer_controller.ex) ---

    private sealed class SubmitBody
    {
        [JsonPropertyName("fee_cents")] public long FeeCents { get; init; }
        [JsonPropertyName("eta_minutes")] public int EtaMinutes { get; init; }
        [JsonPropertyName("note")] public string? Note { get; init; }
    }

    private sealed class WireOffer
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("request_id")] public string? RequestId { get; init; }
        [JsonPropertyName("jeeber_id")] public string? JeeberId { get; init; }
        [JsonPropertyName("fee_cents")] public long FeeCents { get; init; }
        [JsonPropertyName("eta_minutes")] public int EtaMinutes { get; init; }
        [JsonPropertyName("note")] public string? Note { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("edits_count")] public int EditsCount { get; init; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
        [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; init; }
        [JsonPropertyName("withdrawn_at")] public DateTimeOffset? WithdrawnAt { get; init; }
    }

    private sealed class AcceptEnvelope
    {
        [JsonPropertyName("accepted_offer")] public AcceptedOffer? AcceptedOffer { get; init; }
        [JsonPropertyName("rejected_offer_ids")] public List<string>? RejectedOfferIds { get; init; }
        [JsonPropertyName("chat_thread_id")] public string? ChatThreadId { get; init; }
        [JsonPropertyName("otp_code")] public string? OtpCode { get; init; }
    }

    private sealed class AcceptedOffer
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
    }

    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")] public ErrorBody? Error { get; init; }
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
    }
}

/// <summary>
/// Raised when offer-service rejects a submit with HTTP 409. Carries the
/// upstream error <c>code</c> so the adapter can map it onto the gateway's
/// existing <see cref="JeebGateway.Availability.DuplicateOfferException"/> /
/// <see cref="JeebGateway.Availability.TooManyOffersForRequestException"/>
/// surface.
/// </summary>
public sealed class OfferUpstreamConflictException : Exception
{
    public string RequestId { get; }
    public string? UpstreamCode { get; }

    public OfferUpstreamConflictException(string requestId, string? upstreamCode)
        : base($"offer-service rejected the offer on request '{requestId}' with conflict code '{upstreamCode ?? "unknown"}'.")
    {
        RequestId = requestId;
        UpstreamCode = upstreamCode;
    }
}
