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

    public async Task<RequestMirrorResult> MirrorRequestAsync(
        string actingUserId,
        string requestId,
        string clientId,
        CancellationToken ct)
    {
        // OS-1 reads client_id from the BODY (on-behalf-of mirror), so the
        // x-user-id header is informational only here — set it for parity /
        // audit, but the request creator drives ownership.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/requests");
        SetUser(request, actingUserId);
        request.Content = JsonContent.Create(
            new MirrorRequestBody { RequestId = requestId, ClientId = clientId, Status = "open" },
            options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Created:
                return RequestMirrorResult.Created;

            // 200 == idempotent replay (offer-service sets x-idempotency-replay:true).
            case HttpStatusCode.OK:
                return RequestMirrorResult.AlreadyMirrored;

            // 422 (invalid client_id) / 400 (bad request_id uuid): a malformed
            // mirror payload, not a transport fault. Surface as a typed
            // validation error so the store/controller can map it to a 4xx
            // ProblemDetails instead of the global handler's 502.
            case HttpStatusCode.UnprocessableEntity:
            case HttpStatusCode.BadRequest:
            {
                var code = await ReadErrorCodeAsync(response, ct);
                throw new OfferUpstreamValidationException(
                    requestId, (int)response.StatusCode, code, "mirror");
            }

            default:
                // 5xx / unexpected: throw so the global handler surfaces a
                // ProblemDetails 502 rather than silently swallowing the fault.
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException(
                    $"offer-service request-mirror returned unexpected status {(int)response.StatusCode}.");
        }
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

        // GW-2: 404 means the request row was never mirrored into offer-service.
        // Surface a typed signal so the store mirrors-then-retries rather than
        // letting EnsureSuccessStatusCode() raise an HttpRequestException that
        // the global handler would turn into an opaque 502 (the original H1/H2
        // failure mode).
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var code = await ReadErrorCodeAsync(response, ct);
            throw new OfferRequestNotMirroredException(requestId, code);
        }

        // GW-2: 422/400 is a bad submit payload (e.g. fee_cents below the
        // upstream floor) — surface as a typed validation error so the store /
        // controller maps it to a 422 ProblemDetails, not a 502.
        if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
        {
            var code = await ReadErrorCodeAsync(response, ct);
            throw new OfferUpstreamValidationException(
                requestId, (int)response.StatusCode, code, "submit");
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
            JeeberId = wire.AcceptedOffer?.JeeberId,
            ChatThreadId = wire.ChatThreadId,
            OtpCode = wire.OtpCode,
            RejectedOfferIds = wire.RejectedOfferIds ?? new List<string>(),
            Replayed = replayed,
        };
    }

    public async Task<OfferAcceptResult> AcceptWithStatusAsync(
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
        request.Content = JsonContent.Create(new { }, options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);

        // Forward the upstream status VERBATIM — offer-service's FallbackController
        // is the authority for 403/410/409/404. We never re-derive these here.
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
            case HttpStatusCode.Created:
            {
                var wire = await response.Content.ReadFromJsonAsync<AcceptEnvelope>(JsonOptions, ct)
                    ?? throw new HttpRequestException(
                        $"offer-service {response.RequestMessage?.RequestUri} returned an empty accept body.");

                var replayed = response.Headers.TryGetValues("x-idempotency-replay", out var values)
                               && values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

                return new OfferAcceptResult
                {
                    Status = OfferAcceptStatus.Accepted,
                    Envelope = new OfferAcceptWire
                    {
                        AcceptedOfferId = wire.AcceptedOffer?.Id ?? offerId,
                        JeeberId = wire.AcceptedOffer?.JeeberId,
                        ChatThreadId = wire.ChatThreadId,
                        OtpCode = wire.OtpCode,
                        RejectedOfferIds = wire.RejectedOfferIds ?? new List<string>(),
                        Replayed = replayed,
                    },
                };
            }

            case HttpStatusCode.Forbidden:
                return await NegativeAsync(OfferAcceptStatus.NotOwner, response, ct);

            case HttpStatusCode.Gone:
                return await NegativeAsync(OfferAcceptStatus.Expired, response, ct);

            case HttpStatusCode.Conflict:
                return await NegativeAsync(OfferAcceptStatus.Conflict, response, ct);

            case HttpStatusCode.NotFound:
                return await NegativeAsync(OfferAcceptStatus.NotFound, response, ct);

            default:
                // 5xx / unexpected: throw so the global handler surfaces a
                // ProblemDetails rather than a silent mis-map. Do NOT collapse
                // an unexpected upstream status into a fabricated success.
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException(
                    $"offer-service accept returned unexpected status {(int)response.StatusCode}.");
        }
    }

    public async Task<OfferMutationResult> EditAsync(
        string actingUserId,
        string requestId,
        string offerId,
        long? feeCents,
        int? etaMinutes,
        string? note,
        CancellationToken ct)
    {
        // S08 A3: PUT /api/v1/requests/{requestId}/offers/{offerId} (request-scoped,
        // mirroring offer-service router.ex `put "/requests/:request_id/offers/:offer_id"`).
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/v1/requests/{Uri.EscapeDataString(requestId)}/offers/{Uri.EscapeDataString(offerId)}");
        SetUser(request, actingUserId);
        // Only send the fields the caller supplied — a partial edit (A3 sends fee
        // only) must leave eta/note untouched. The upstream changeset ignores nulls;
        // omitting them keeps the contract a true PATCH-over-PUT.
        request.Content = JsonContent.Create(
            new EditBody { FeeCents = feeCents, EtaMinutes = etaMinutes, Note = note },
            options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
            case HttpStatusCode.Created:
                return new OfferMutationResult
                {
                    Status = OfferMutationStatus.Ok,
                    Offer = await DeserializeOfferAsync(response, ct),
                };

            case HttpStatusCode.Forbidden:
                return await MutationNegativeAsync(OfferMutationStatus.NotOwner, response, ct);

            case HttpStatusCode.NotFound:
                return await MutationNegativeAsync(OfferMutationStatus.NotFound, response, ct);

            // 409 (not editable / edit-cap reached) and 422/410 (edit window closed)
            // all mean "no longer mutable" — forward as Conflict.
            case HttpStatusCode.Conflict:
            case HttpStatusCode.UnprocessableEntity:
            case HttpStatusCode.Gone:
                return await MutationNegativeAsync(OfferMutationStatus.Conflict, response, ct);

            default:
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException(
                    $"offer-service edit returned unexpected status {(int)response.StatusCode}.");
        }
    }

    public async Task<OfferMutationResult> RejectAsync(
        string actingUserId,
        string offerId,
        CancellationToken ct)
    {
        // S08 A5: POST /api/v1/offers/{offerId}/reject (offer-scoped, mirroring the
        // S07 accept_by_offer route). offer-service owns the reject saga + the
        // StateMachine :reject transition; the gateway forwards the actor and the
        // status verbatim. The "api/v1/" prefix is REQUIRED — the offer client's
        // BaseAddress (Services:Offer:BaseUrl) is host:port with NO path, and every
        // other method here carries "api/v1/" (submit line 53/100, accept line
        // 177/214). Omitting it resolved to /offers/{id}/reject which offer-service
        // does not route -> live 404 (the route exists only under the /api/v1 scope,
        // router.ex:49). Caught at the deploy smoke gate, not by unit tests.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"api/v1/offers/{Uri.EscapeDataString(offerId)}/reject");
        SetUser(request, actingUserId);
        request.Content = JsonContent.Create(new { }, options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
            case HttpStatusCode.Created:
            case HttpStatusCode.NoContent:
                // Reject returns no offer body in the contract — the status alone is
                // the outcome. Do not attempt to deserialize an offer projection.
                return new OfferMutationResult { Status = OfferMutationStatus.Ok };

            case HttpStatusCode.Forbidden:
                return await MutationNegativeAsync(OfferMutationStatus.NotOwner, response, ct);

            case HttpStatusCode.NotFound:
                return await MutationNegativeAsync(OfferMutationStatus.NotFound, response, ct);

            // 409 (already rejected / not rejectable) and 410/422 (window closed)
            // all mean "no longer rejectable" — forward as Conflict.
            case HttpStatusCode.Conflict:
            case HttpStatusCode.Gone:
            case HttpStatusCode.UnprocessableEntity:
                return await MutationNegativeAsync(OfferMutationStatus.Conflict, response, ct);

            default:
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException(
                    $"offer-service reject returned unexpected status {(int)response.StatusCode}.");
        }
    }

    private static async Task<OfferMutationResult> MutationNegativeAsync(
        OfferMutationStatus status, HttpResponseMessage response, CancellationToken ct)
        => new() { Status = status, UpstreamCode = await ReadErrorCodeAsync(response, ct) };

    private static async Task<OfferAcceptResult> NegativeAsync(
        OfferAcceptStatus status, HttpResponseMessage response, CancellationToken ct)
        => new() { Status = status, UpstreamCode = await ReadErrorCodeAsync(response, ct) };

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
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // Empty / non-JSON negative body — the status alone drives the
            // gateway response, the code is logging-only.
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

    /// <summary>
    /// S08 A3 edit body. Nullable so a partial edit only sends the supplied fields;
    /// the configured <see cref="JsonOptions"/> emits nulls, which the offer-service
    /// changeset ignores, so an absent field is left unchanged.
    /// </summary>
    private sealed class EditBody
    {
        [JsonPropertyName("fee_cents")] public long? FeeCents { get; init; }
        [JsonPropertyName("eta_minutes")] public int? EtaMinutes { get; init; }
        [JsonPropertyName("note")] public string? Note { get; init; }
    }

    private sealed class MirrorRequestBody
    {
        [JsonPropertyName("request_id")] public string RequestId { get; init; } = string.Empty;
        [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; init; } = "open";
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
        [JsonPropertyName("jeeber_id")] public string? JeeberId { get; init; }
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

/// <summary>
/// Raised when offer-service rejects a submit with HTTP 404 — the request row
/// has never been mirrored into offer-service. Carries the upstream error
/// <c>code</c> for diagnostics. The <see cref="UpstreamPendingOffersStore"/>
/// catches this, mirrors the request via
/// <see cref="IOfferServiceClient.MirrorRequestAsync"/>, then retries the submit
/// exactly once — the GW-1 self-heal that closes the S07 submit path. If a
/// submit 404s again <em>after</em> a successful mirror, the exception is allowed
/// to surface (a genuine not-found, not a missing-mirror) so it maps to a 404
/// rather than looping.
/// </summary>
public sealed class OfferRequestNotMirroredException : Exception
{
    public string RequestId { get; }
    public string? UpstreamCode { get; }

    public OfferRequestNotMirroredException(string requestId, string? upstreamCode)
        : base($"offer-service has no mirrored request '{requestId}' (upstream code '{upstreamCode ?? "not_found"}').")
    {
        RequestId = requestId;
        UpstreamCode = upstreamCode;
    }
}

/// <summary>
/// Raised when offer-service rejects a mirror or submit with HTTP 422/400 — a
/// payload validation failure (invalid <c>client_id</c>, malformed
/// <c>request_id</c>, or a <c>fee_cents</c> below the upstream floor). Carries
/// the upstream status and error <c>code</c> so the gateway maps it to a 422
/// ProblemDetails (a caller-correctable 4xx) rather than collapsing it into a
/// 502 via <c>EnsureSuccessStatusCode()</c>.
/// </summary>
public sealed class OfferUpstreamValidationException : Exception
{
    public string RequestId { get; }
    public int UpstreamStatus { get; }
    public string? UpstreamCode { get; }

    /// <summary>Which call produced the validation failure: <c>"mirror"</c> or <c>"submit"</c>.</summary>
    public string Stage { get; }

    public OfferUpstreamValidationException(
        string requestId, int upstreamStatus, string? upstreamCode, string stage)
        : base($"offer-service rejected the {stage} for request '{requestId}' with {upstreamStatus} " +
               $"(code '{upstreamCode ?? "validation_error"}').")
    {
        RequestId = requestId;
        UpstreamStatus = upstreamStatus;
        UpstreamCode = upstreamCode;
        Stage = stage;
    }
}
