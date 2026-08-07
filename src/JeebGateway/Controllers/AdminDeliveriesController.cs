using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Conversations.Client;
using JeebGateway.Financials;
using JeebGateway.Services.Cdn;
using JeebGateway.Services.Clients;
using JeebGateway.Services.Generated.GeolocationService;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Stable operator aggregate over delivery-service and bounded evidence reads.
/// Every source remains authoritative for its own data; failures are represented
/// explicitly so the UI never mistakes missing evidence for an empty record.
/// </summary>
[ApiController]
public sealed class AdminDeliveriesController : ControllerBase
{
    private const string OwnerClient = "admin-deliveries-owner";
    private static readonly TimeSpan MfaFreshness = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions AdminJson = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> DeliveryOwnerFields = new(StringComparer.Ordinal)
    {
        // Page envelopes.
        "data", "deliveries", "page", "next_cursor", "cursor", "limit", "total",
        // Delivery resource.
        "delivery_id", "tenant_id", "party_ids", "client_id", "courier_id",
        "current_status", "tier_id", "pickup_lat", "pickup_lng", "evidence_url",
        "allowed_operations", "operation", "target_status", "expected_status",
        "started_at", "created_at", "updated_at",
        // Timeline resource. Geo is deliberately reduced to scalar coordinates.
        "timeline", "transition_id", "from_status", "to_status", "trigger", "source",
        "actor_id", "geo", "lat", "lng", "accuracy_m", "recorded_at", "transitioned_at",
    };
    private readonly IHttpClientFactory _clients;
    private readonly IGenericCaseGatewayService _cases;
    private readonly IGeolocationServiceClient _geolocation;
    private readonly IGeoHistoryClient _geoHistory;
    private readonly IJeebConversationClient _chat;
    private readonly IAdminSettlementPortalService _settlements;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;

    public AdminDeliveriesController(
        IHttpClientFactory clients,
        IGenericCaseGatewayService cases,
        IGeolocationServiceClient geolocation,
        IGeoHistoryClient geoHistory,
        IJeebConversationClient chat,
        IAdminSettlementPortalService settlements,
        IConfiguration configuration,
        TimeProvider? clock = null)
    {
        _clients = clients;
        _cases = cases;
        _geolocation = geolocation;
        _geoHistory = geoHistory;
        _chat = chat;
        _settlements = settlements;
        _configuration = configuration;
        _clock = clock ?? TimeProvider.System;
    }

    [HttpGet("admin/v1/deliveries")]
    [RequireCapability(Capabilities.AdminDeliveriesRead)]
    [ProducesResponseType(typeof(AdminDeliveryListResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Index(
        [FromQuery(Name = "query"), StringLength(200)] string? query,
        [FromQuery(Name = "status"), StringLength(64)] string? status,
        [FromQuery(Name = "clientId"), StringLength(255)] string? clientId,
        [FromQuery(Name = "jeeberId"), StringLength(255)] string? jeeberId,
        [FromQuery(Name = "createdFrom")] string? createdFrom,
        [FromQuery(Name = "createdTo")] string? createdTo,
        [FromQuery(Name = "sort"), StringLength(16)] string? sort,
        [FromQuery(Name = "limit"), Range(1, 200)] int? limit,
        [FromQuery(Name = "cursor"), StringLength(2048)] string? cursor,
        CancellationToken ct) =>
        RelayDeliveryAsync("api/v1/admin/deliveries" + Request.QueryString, null, ct);

    [HttpGet("admin/v1/deliveries/{deliveryId}/timeline")]
    [RequireCapability(Capabilities.AdminDeliveriesRead)]
    [ProducesResponseType(typeof(AdminDeliveryTimelineResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Timeline(
        string deliveryId,
        [FromQuery(Name = "limit"), Range(1, 200)] int? limit,
        [FromQuery(Name = "cursor"), StringLength(2048)] string? cursor,
        CancellationToken ct) =>
        RelayDeliveryAsync(
            $"api/v1/admin/deliveries/{Uri.EscapeDataString(deliveryId)}/timeline{Request.QueryString}",
            deliveryId,
            ct);

    [HttpPost("admin/v1/deliveries/{deliveryId}/transition")]
    [RequireCapability(Capabilities.AdminDeliveriesOperate)]
    [ProducesResponseType(typeof(AdminDeliveryTransitionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Transition(
        string deliveryId,
        [FromBody] AdminDeliveryTransitionRequest? request,
        [FromHeader(Name = "Idempotency-Key"), StringLength(200, MinimumLength = 8)] string? idempotencyKey,
        CancellationToken ct)
    {
        PreventCaching();
        if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Trim().Length > 128
            || request is null
            || !Bounded(request.To, 64, 1)
            || !Bounded(request.ExpectedStatus, 64, 1)
            || !Bounded(request.Reason, 1_000, 3))
            return Problem(
                title: "A bounded target, expectedStatus, and reason are required.",
                statusCode: StatusCodes.Status400BadRequest);
        if (!Bounded(idempotencyKey, 200, 8))
            return Problem(
                title: "A valid Idempotency-Key is required.",
                statusCode: StatusCodes.Status400BadRequest);
        if (!HasFreshMfa())
            return Problem(
                type: "https://jeeb.dev/errors/mfa-required",
                title: "Fresh multi-factor authentication is required.",
                statusCode: StatusCodes.Status403Forbidden);
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
            return unauthorized;

        var client = _clients.CreateClient(OwnerClient);
        if (client.BaseAddress is null) return DeliveryOwnerUnavailable();
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/admin/deliveries/{Uri.EscapeDataString(deliveryId.Trim())}/transition")
        {
            Content = JsonContent.Create(new
            {
                to = request.To!.Trim(),
                expected_status = request.ExpectedStatus!.Trim(),
                reason = request.Reason!.Trim(),
            }),
        };
        message.Headers.TryAddWithoutValidation("X-Admin-Id", adminId);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey!.Trim());

        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode >= 500 || payload.Length > 1_000_000 || !IsJson(payload))
                return Problem(
                    type: "https://jeeb.dev/errors/delivery-owner-unavailable",
                    title: "The delivery owner returned an invalid response.",
                    statusCode: StatusCodes.Status502BadGateway);
            if (response.Headers.TryGetValues("Idempotency-Replayed", out var replayed))
                Response.Headers["Idempotency-Replayed"] = replayed.FirstOrDefault();
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = payload,
                ContentType = "application/json",
            };
        }
        catch (Exception error) when (error is HttpRequestException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return DeliveryOwnerUnavailable();
        }
    }

    [HttpGet("admin/v1/deliveries/{deliveryId}")]
    [RequireCapability(Capabilities.AdminDeliveriesRead)]
    [ProducesResponseType(typeof(AdminDeliveryAggregateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail(string deliveryId, CancellationToken ct)
    {
        var deliveryClient = _clients.CreateClient(OwnerClient);
        if (deliveryClient.BaseAddress is null) return DependencyUnavailable("delivery");

        var escaped = Uri.EscapeDataString(deliveryId);
        var deliveryResult = await FetchJsonAsync(deliveryClient, $"api/v1/admin/deliveries/{escaped}", ct);
        if (deliveryResult.StatusCode == StatusCodes.Status404NotFound) return NotFound();
        if (deliveryResult.Node is null) return DependencyUnavailable("delivery");

        // Delivery-service responses are an internal owner contract, not a safe
        // browser representation. Project the document to the explicitly frozen
        // admin schema before it can be composed with any other evidence source.
        // This prevents a future owner field (object key, signed URL, bucket path,
        // internal metadata, etc.) from silently becoming public by passthrough.
        SanitizeDeliveryOwnerDocument(deliveryResult.Node);

        var clientId = deliveryResult.Node["party_ids"]?["client_id"]?.GetValue<string>();
        var timelineTask = FetchJsonAsync(deliveryClient,
            $"api/v1/admin/deliveries/{escaped}/timeline?limit=200", ct);
        var casesTask = HasCapability(Capabilities.AdminCasesRead)
            ? FetchCasesAsync(deliveryId, ct)
            : Task.FromResult(new SourceResult(
                null, StatusCodes.Status403Forbidden, "admin_cases_read_required"));
        var settlementTask = HasCapability(Capabilities.AdminSettlementsRead)
            ? FetchSettlementsAsync(deliveryId, ct)
            : Task.FromResult(new SourceResult(
                null, StatusCodes.Status403Forbidden, "admin_settlements_read_required"));
        var routeTask = FetchRouteAsync(deliveryId, ct);
        var chatTask = FetchChatAsync(deliveryId, clientId, ct);

        await Task.WhenAll(timelineTask, casesTask, settlementTask, routeTask, chatTask);
        var timeline = await timelineTask;
        var caseEvidence = await casesTask;
        var settlements = await settlementTask;
        // A courier's current location is not delivery-scoped evidence and can
        // reveal live whereabouts when an operator opens an old delivery.
        var location = new SourceResult(
            null, StatusCodes.Status422UnprocessableEntity, "delivery_scoped_location_unavailable");
        var route = await routeTask;
        var chat = await chatTask;

        RewriteEvidenceReferences(deliveryResult.Node, deliveryId);
        if (timeline.Node is not null)
        {
            SanitizeDeliveryOwnerDocument(timeline.Node);
            RewriteEvidenceReferences(timeline.Node, deliveryId);
        }

        var sourceHealth = new Dictionary<string, object?>
        {
            ["delivery"] = Source("available", null),
            ["timeline"] = Source(timeline.Node is null ? "unavailable" : "available", timeline.Error),
            ["cases"] = Source(caseEvidence.Node is null ? "unavailable" : "available", caseEvidence.Error),
            ["settlements"] = Source(settlements.Node is null ? "unavailable" : "available", settlements.Error),
            ["location"] = Source(location.Node is null ? "unavailable" : "available", location.Error),
            ["route"] = Source(route.Node is null ? "unavailable" : "available", route.Error),
            ["chat"] = Source(chat.Node is null ? "unavailable" : "available", chat.Error),
        };

        var partial = sourceHealth.Values.Any(value =>
            value is Dictionary<string, object?> state
            && !string.Equals(state["status"]?.ToString(), "available", StringComparison.Ordinal));

        var fieldAvailability = new Dictionary<string, object?>
        {
            ["requestRef"] = FieldUnavailable(),
            ["offerRef"] = FieldUnavailable(),
            ["bundleRef"] = FieldUnavailable(),
            ["destination"] = FieldUnavailable(),
            ["deliveryNotes"] = FieldUnavailable(),
            ["otpConfirmation"] = FieldUnavailable(),
            ["proofOfDeliveryMetadata"] = FieldUnavailable(),
        };

        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            data = new
            {
                delivery = deliveryResult.Node,
                timeline = timeline.Node,
                evidence = new
                {
                    proof = deliveryResult.Node["evidence_url"],
                    route = route.Node,
                    latestCourierLocation = location.Node,
                    relatedCases = caseEvidence.Node,
                    codSettlements = settlements.Node,
                    chat = chat.Node,
                },
                sourceHealth,
                fieldAvailability,
                partial = partial || fieldAvailability.Count > 0,
            }
        });
    }

    /// <summary>
    /// Streams one proof object through the gateway after proving the opaque token
    /// still belongs to the requested delivery in delivery-service. The browser
    /// never receives a storage URL, signed URL, bucket name, or raw object ref.
    /// </summary>
    [HttpGet("admin/v1/deliveries/{deliveryId}/evidence/{token}")]
    [RequireCapability(Capabilities.AdminDeliveriesRead)]
    public async Task<IActionResult> Evidence(string deliveryId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return BadRequest();
        if (string.IsNullOrWhiteSpace(EvidenceTokenKey()))
            return DependencyUnavailable("evidence token service");

        var deliveryClient = _clients.CreateClient(OwnerClient);
        if (deliveryClient.BaseAddress is null) return DependencyUnavailable("delivery");

        var escaped = Uri.EscapeDataString(deliveryId);
        var detailTask = FetchJsonAsync(deliveryClient, $"api/v1/admin/deliveries/{escaped}", ct);
        var timelineTask = FetchJsonAsync(
            deliveryClient, $"api/v1/admin/deliveries/{escaped}/timeline?limit=200", ct);
        await Task.WhenAll(detailTask, timelineTask);
        var detail = await detailTask;
        var timeline = await timelineTask;
        if (detail.StatusCode == StatusCodes.Status404NotFound) return NotFound();
        if (detail.Node is null) return DependencyUnavailable("delivery");

        var reference = EnumerateEvidenceReferences(detail.Node)
            .Concat(timeline.Node is null
                ? Enumerable.Empty<string>()
                : EnumerateEvidenceReferences(timeline.Node))
            .FirstOrDefault(candidate => TokenMatches(token, CreateEvidenceToken(deliveryId, candidate)));
        if (reference is null) return NotFound();

        var cdn = _clients.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (cdn.BaseAddress is null) return DependencyUnavailable("asset store");
        if (!TryGetOwnedObjectReference(reference, cdn.BaseAddress, out var objectReference))
            return NotFound();

        var upstreamUri = new Uri(
            cdn.BaseAddress,
            CdnUploadUrlResolver.CdnFetchPathPrefix + Uri.EscapeDataString(objectReference));
        if (!CdnUploadUrlResolver.IsOnFetchPrefix(upstreamUri, cdn.BaseAddress)) return BadRequest();

        HttpResponseMessage upstream;
        try
        {
            upstream = await cdn.GetAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception error) when (error is HttpRequestException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return DependencyUnavailable("asset store");
        }

        HttpContext.Response.RegisterForDispose(upstream);
        Response.Headers.CacheControl = "private, no-store";
        if (upstream.StatusCode == HttpStatusCode.NotFound) return NotFound();
        if (!upstream.IsSuccessStatusCode) return DependencyUnavailable("asset store");

        if (!AdminEvidenceResponsePolicy.HasSafeLength(upstream.Content.Headers.ContentLength))
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (!AdminEvidenceResponsePolicy.TryApply(
                Response, upstream.Content.Headers.ContentType?.ToString(), out var contentType))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var declaredLength = upstream.Content.Headers.ContentLength!.Value;
        var stream = await upstream.Content.ReadAsStreamAsync(ct);
        return File(AdminEvidenceResponsePolicy.EnforceDeclaredLength(stream, declaredLength), contentType);
    }

    private async Task<IActionResult> RelayDeliveryAsync(
        string relativeUrl, string? deliveryId, CancellationToken ct)
    {
        var client = _clients.CreateClient(OwnerClient);
        if (client.BaseAddress is null) return DependencyUnavailable("delivery");
        try
        {
            using var response = await client.GetAsync(relativeUrl, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    var node = JsonNode.Parse(payload);
                    if (node is not null)
                    {
                        SanitizeDeliveryOwnerDocument(node);
                        RewriteEvidenceReferences(node, deliveryId);
                        payload = node.ToJsonString();
                    }
                }
                catch (JsonException)
                {
                    return DependencyUnavailable("delivery");
                }
            }
            Response.Headers.CacheControl = "no-store";
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = payload,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            };
        }
        catch (Exception error) when (error is HttpRequestException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return DependencyUnavailable("delivery");
        }
    }

    private void RewriteEvidenceReferences(JsonNode node, string? inheritedDeliveryId)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var currentDeliveryId = ReadString(obj, "delivery_id")
                                            ?? inheritedDeliveryId;
                    foreach (var property in obj.ToList())
                    {
                        if (string.Equals(property.Key, "evidence_url", StringComparison.OrdinalIgnoreCase))
                        {
                            var raw = property.Value is JsonValue value && value.TryGetValue<string>(out var text)
                                ? text
                                : null;
                            obj[property.Key] = string.IsNullOrWhiteSpace(raw)
                                                || string.IsNullOrWhiteSpace(currentDeliveryId)
                                                || string.IsNullOrWhiteSpace(EvidenceTokenKey())
                                ? null
                                : $"/gateway/admin/v1/deliveries/{Uri.EscapeDataString(currentDeliveryId)}/evidence/{CreateEvidenceToken(currentDeliveryId, raw)}";
                        }
                        else if (property.Value is not null)
                        {
                            RewriteEvidenceReferences(property.Value, currentDeliveryId);
                        }
                    }
                    break;
                }
            case JsonArray array:
                foreach (var child in array)
                    if (child is not null) RewriteEvidenceReferences(child, inheritedDeliveryId);
                break;
        }
    }

    private static void SanitizeDeliveryOwnerDocument(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (!DeliveryOwnerFields.Contains(property.Key))
                    {
                        obj.Remove(property.Key);
                        continue;
                    }

                    if (property.Value is not null)
                        SanitizeDeliveryOwnerDocument(property.Value);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                    if (child is not null) SanitizeDeliveryOwnerDocument(child);
                break;
        }
    }

    private bool HasFreshMfa()
    {
        var methods = User.FindAll("amr")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!methods.Any(method => string.Equals(method, "mfa", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(method, "otp", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(method, "webauthn", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!long.TryParse(User.FindFirst("auth_time")?.Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            return false;
        var age = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unix);
        return age >= TimeSpan.Zero && age <= MfaFreshness;
    }

    private static bool Bounded(string? value, int max, int min) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= min && value.Trim().Length <= max;

    private static bool IsJson(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var _ = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void PreventCaching()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private static string? ReadString(JsonObject obj, string propertyName) =>
        obj[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static IEnumerable<string> EnumerateEvidenceReferences(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (string.Equals(property.Key, "evidence_url", StringComparison.OrdinalIgnoreCase)
                    && property.Value is JsonValue value
                    && value.TryGetValue<string>(out var reference)
                    && !string.IsNullOrWhiteSpace(reference))
                {
                    yield return reference;
                }

                if (property.Value is not null)
                    foreach (var child in EnumerateEvidenceReferences(property.Value))
                        yield return child;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var childNode in array)
                if (childNode is not null)
                    foreach (var child in EnumerateEvidenceReferences(childNode))
                        yield return child;
        }
    }

    private string CreateEvidenceToken(string deliveryId, string reference)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(EvidenceTokenKey()));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{deliveryId}\n{reference}"));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private string EvidenceTokenKey() =>
        _configuration["AdminEvidence:TokenKey"] ?? string.Empty;

    private static bool TokenMatches(string supplied, string expected)
    {
        var suppliedBytes = Encoding.ASCII.GetBytes(supplied);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static bool TryGetOwnedObjectReference(
        string reference, Uri cdnBaseAddress, out string objectReference)
    {
        objectReference = string.Empty;
        var candidate = reference.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            if ((absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                || !string.Equals(absolute.Host, cdnBaseAddress.Host, StringComparison.OrdinalIgnoreCase)
                || absolute.Port != cdnBaseAddress.Port)
                return false;

            var marker = "/" + CdnUploadUrlResolver.CdnFetchPathPrefix;
            if (!absolute.AbsolutePath.StartsWith(marker, StringComparison.Ordinal)) return false;
            candidate = Uri.UnescapeDataString(absolute.AbsolutePath[marker.Length..]);
        }

        candidate = candidate.TrimStart('/');
        if (candidate.Length is not (> 0 and <= 512)
            || candidate.Contains("..", StringComparison.Ordinal)
            || candidate.Contains('%')
            || candidate.Contains('\\')
            || candidate.Contains('?')
            || candidate.Contains('#'))
            return false;
        objectReference = candidate;
        return true;
    }

    private async Task<SourceResult> FetchCasesAsync(string deliveryId, CancellationToken ct)
    {
        try
        {
            var page = await _cases.ListAdminAsync(new GenericCaseQueryV1
            {
                SubjectType = "delivery",
                SubjectRef = deliveryId,
                Limit = 50,
                Sort = "sla",
            }, null, ct);
            return new SourceResult(JsonSerializer.SerializeToNode(page, AdminJson), StatusCodes.Status200OK, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new SourceResult(null, StatusCodes.Status503ServiceUnavailable, "case_source_unavailable");
        }
    }

    private bool HasCapability(string capability)
    {
        var allowed = CapabilityRolePolicy.RolesFor(capability);
        return UserIdentity.GetRoles(HttpContext)
            .Select(JeebRoleTranslator.ToContract)
            .Any(role => allowed.Any(candidate =>
                string.Equals(role, candidate, StringComparison.OrdinalIgnoreCase)));
    }

    // Extraction adaptation: the PR proxied this to the retired UPG owner; the
    // in-gateway COD settlement owner serves the identical page shape instead.
    private async Task<SourceResult> FetchSettlementsAsync(string deliveryId, CancellationToken ct)
    {
        try
        {
            var page = await _settlements.ListAsync(
                new Financials.AdminSettlementPortalListRequest(
                    Query: null, Status: null, ProviderId: null, DeliveryId: deliveryId,
                    From: null, To: null, Sort: null, Limit: 20, Cursor: null),
                ct);
            return new SourceResult(
                JsonSerializer.SerializeToNode(page, AdminJson), StatusCodes.Status200OK, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new SourceResult(
                null, StatusCodes.Status503ServiceUnavailable, "settlement_source_unavailable");
        }
    }

    private async Task<SourceResult> FetchRouteAsync(string deliveryId, CancellationToken ct)
    {
        const int limit = 500;
        try
        {
            var page = await _geoHistory.GetTrackHistoryPageAsync(deliveryId, null, limit, ct);
            if (!page.Available)
                return new SourceResult(null, StatusCodes.Status404NotFound, "route_not_found");

            var retentionDays = Math.Clamp(page.RetentionDays, 1, 30);
            var now = _clock.GetUtcNow();
            var retainedFrom = page.RetainedFrom > now.AddDays(-30)
                ? page.RetainedFrom
                : now.AddDays(-30);
            var pings = page.Pings
                .Where(point => point.RecordedAt >= retainedFrom && point.RecordedAt <= now)
                .ToArray();
            var node = JsonSerializer.SerializeToNode(new
            {
                trackId = page.TrackId ?? deliveryId,
                retainedFrom,
                retentionDays,
                pings,
                page.HasMore,
                page.NextCursor,
                truncated = page.HasMore,
                limit,
            }, AdminJson);
            return new SourceResult(node, StatusCodes.Status200OK, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new SourceResult(null, StatusCodes.Status503ServiceUnavailable, "route_source_unavailable");
        }
    }

    private async Task<SourceResult> FetchChatAsync(
        string deliveryId, string? deliveryClientId, CancellationToken ct)
    {
        const int limit = 200;
        if (string.IsNullOrWhiteSpace(deliveryClientId))
            return new SourceResult(null, StatusCodes.Status422UnprocessableEntity,
                "delivery_client_missing");

        try
        {
            var conversation = await _chat.GetConversationByCorrelationAsync(deliveryId, ct);
            var activeClient = conversation.Participants.SingleOrDefault(participant =>
                participant.RemovedAt is null
                && string.Equals(participant.RoleInConvo, "client", StringComparison.Ordinal)
                && string.Equals(participant.UserId, deliveryClientId, StringComparison.Ordinal));
            if (activeClient is null)
                return new SourceResult(null, StatusCodes.Status403Forbidden,
                    "active_client_participant_missing");

            var page = await _chat.ExportMessagesForViewerAsync(
                conversation.ConversationId, activeClient.UserId, null, null, limit, ct);
            var node = JsonSerializer.SerializeToNode(new
            {
                conversationId = conversation.ConversationId,
                phase = conversation.Phase,
                page.AsOf,
                messages = page.Messages.Select(message => new
                {
                    id = message.MessageId,
                    message.Kind,
                    message.Subtype,
                    author = message.AuthorId,
                    message.Body,
                    created_at = message.CreatedAt,
                }).ToArray(),
                page.HasMore,
                page.NextCursor,
                truncated = page.HasMore,
                limit,
            }, AdminJson);
            return new SourceResult(node, StatusCodes.Status200OK, null);
        }
        catch (JeebConversationApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new SourceResult(JsonSerializer.SerializeToNode(new
            {
                conversationId = (string?)null,
                messages = Array.Empty<object>(),
                hasMore = false,
                nextCursor = (string?)null,
                truncated = false,
                limit,
            }, AdminJson), StatusCodes.Status200OK, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new SourceResult(null, StatusCodes.Status503ServiceUnavailable, "chat_source_unavailable");
        }
    }

    private static async Task<SourceResult> FetchJsonAsync(
        HttpClient client, string relativeUrl, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        return await FetchJsonAsync(client, message, ct);
    }

    private static async Task<SourceResult> FetchJsonAsync(
        HttpClient client, HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            using (message)
            using (var response = await client.SendAsync(message, ct))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var node = response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body)
                    ? JsonNode.Parse(body)
                    : null;
                return new SourceResult(node, (int)response.StatusCode,
                    response.IsSuccessStatusCode ? null : $"http_{(int)response.StatusCode}");
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
                                      || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new SourceResult(null, StatusCodes.Status503ServiceUnavailable, "upstream_unavailable");
        }
    }

    private ObjectResult DependencyUnavailable(string source) => Problem(
        type: "https://jeeb.dev/errors/dependency-unavailable",
        title: $"The {source} source is unavailable.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private ObjectResult DeliveryOwnerUnavailable() => Problem(
        type: "https://jeeb.dev/errors/delivery-owner-unavailable",
        title: "The delivery owner is unavailable.",
        statusCode: StatusCodes.Status502BadGateway);

    private static Dictionary<string, object?> Source(string status, string? reason) => new()
    {
        ["status"] = status,
        ["reason"] = reason,
    };

    private static Dictionary<string, object?> FieldUnavailable() => new()
    {
        ["status"] = "unavailable",
        ["source"] = "delivery_owner_contract_missing",
    };

    private sealed record SourceResult(JsonNode? Node, int StatusCode, string? Error);
}
