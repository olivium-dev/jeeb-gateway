using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IKycServiceClient"/>. The named
/// "kyc" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies
/// BaseAddress + the org-standard bearer / X-Service-Auth / Polly resilience
/// chain, so this class never has to think about retry/timeout/circuit-breaker.
///
/// Hand-coded against the kyc-service contract S03 requires because kyc-service
/// is net-new and exposes no reachable OpenAPI doc yet (the repo is owner/SSH
/// ESCALATED — ADR-0004). Mirrors the camelCase System.Text.Json convention the
/// other thin clients bind against. Replace with an NSwag-generated client once
/// kyc-service publishes a reachable spec, keeping <see cref="IKycServiceClient"/>
/// as the gateway-facing seam.
/// </summary>
public sealed class KycServiceClient : IKycServiceClient
{
    private const string IdempotencyHeader = "Idempotency-Key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly HttpClient _http;

    public KycServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<KycSubmitResult> SubmitAsync(
        KycSubmitUpstreamPayload payload,
        string idempotencyKey,
        CancellationToken ct)
    {
        // Map the gateway-facing rich payload onto the LIVE kyc-service wire
        // contract (KycSubmitRequest: subject, vehicleType, vehicleRegistration,
        // idFrontRef, idBackRef, selfieRef, grantsRole). The Jeeb role-grant
        // INTENT ("jeeber") is set here by the gateway (it owns Jeeb semantics,
        // BR-1); kyc-service stores it and echoes it back on approve. `subject`
        // is the owning user id.
        var wire = new KycSubmitWire
        {
            Subject = payload.UserId,
            VehicleType = payload.VehicleYearMakeModel,
            VehicleRegistration = payload.VehicleRegistrationRef ?? payload.VehiclePlateNumber,
            IdFrontRef = payload.IdDocumentFrontRef,
            IdBackRef = payload.IdDocumentBackRef,
            SelfieRef = payload.SelfieWithLivenessRef,
            GrantsRole = JeeberRole,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/kyc/submissions")
        {
            Content = JsonContent.Create(wire, options: JsonOptions),
        };
        AddIdempotencyKey(request, idempotencyKey);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        // 200 = idempotent replay of an existing row; 201 = new row (N9).
        var replayed = response.StatusCode == HttpStatusCode.OK;
        var doc = await ReadJsonAsync(response, ct);

        return new KycSubmitResult
        {
            SubmissionId = ReadString(doc, "id", "submissionId") ?? throw EmptyId(response),
            State = ReadString(doc, "status", "state") ?? "Submitted",
            TosSignedAt = ReadDate(doc, "tosSignedAt", "tos_signed_at"),
            TosAcceptedVersion = ReadString(doc, "tosAcceptedVersion", "tos_accepted_version"),
            Replayed = replayed,
            Document = doc,
        };
    }

    public async Task<KycTosSignatureResult> StampTosSignatureAsync(
        string submissionId,
        KycTosStampPayload payload,
        string idempotencyKey,
        CancellationToken ct)
    {
        // LIVE kyc-service wire contract (TosSignatureRequest: signatureBlob,
        // acceptedVersion). The gateway never forwards the RAW signature blob into
        // the KYC store — it passes the contract-signing-minted proof ref as the
        // verifiable handle (DEC3); kyc-service treats it as the opaque blob ref.
        var wire = new KycTosSignatureWire
        {
            SignatureBlob = payload.SignatureProofRef,
            AcceptedVersion = payload.TosAcceptedVersion,
        };

        var path = $"v1/kyc/submissions/{Uri.EscapeDataString(submissionId)}/tos-signature";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(wire, options: JsonOptions),
        };
        AddIdempotencyKey(request, idempotencyKey);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var replayed = response.StatusCode == HttpStatusCode.OK;
        var doc = await ReadJsonAsync(response, ct);

        var signedAt = ReadDate(doc, "tosSignedAt", "tos_signed_at")
            ?? throw new HttpRequestException(
                $"kyc-service tos-signature for '{submissionId}' returned no tos_signed_at.");

        return new KycTosSignatureResult
        {
            TosSignedAt = signedAt,
            TosAcceptedVersion = ReadString(doc, "tosAcceptedVersion", "tos_accepted_version")
                ?? payload.TosAcceptedVersion,
            Replayed = replayed,
        };
    }

    public async Task<KycSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        var path = $"v1/kyc/submissions/by-user/{Uri.EscapeDataString(userId)}";
        return await GetSubmissionOrNullAsync(path, ct);
    }

    public async Task<KycSubmissionView?> GetByIdAsync(string submissionId, CancellationToken ct)
    {
        var path = $"v1/kyc/submissions/{Uri.EscapeDataString(submissionId)}";
        return await GetSubmissionOrNullAsync(path, ct);
    }

    public async Task<KycQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
    {
        var path = $"v1/kyc/submissions?status=pending_review&page={page}&pageSize={pageSize}";
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();

        var doc = await ReadJsonAsync(response, ct);
        var items = new List<KycSubmissionView>();
        if (doc.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in arr.EnumerateArray())
            {
                items.Add(MapSubmission(element));
            }
        }

        return new KycQueuePage
        {
            Items = items,
            Page = ReadInt(doc, "page") ?? page,
            PageSize = ReadInt(doc, "pageSize") ?? pageSize,
            Total = ReadInt(doc, "total") ?? items.Count,
        };
    }

    public async Task<KycReviewDecision> ReviewAsync(
        string submissionId,
        KycReviewDecisionRequest request,
        CancellationToken ct)
    {
        // LIVE kyc-service wire contract (KycReviewRequest: action, reason,
        // resubmitSlots, reviewerId).
        var wire = new KycReviewWire
        {
            Action = ToWireAction(request.Action),
            Reason = request.Reason,
            ResubmitSlots = request.ResubmitSteps,
            ReviewerId = request.ReviewerId,
        };

        var path = $"v1/kyc/submissions/{Uri.EscapeDataString(submissionId)}/review";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(wire, options: JsonOptions),
        };

        using var response = await _http.SendAsync(httpRequest, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KycReviewConflictException(submissionId, await ReadProblemDetailAsync(response, ct));
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new KycReviewValidationException(
                await ReadProblemDetailAsync(response, ct)
                ?? $"kyc-service rejected the review of '{submissionId}'.");
        }
        response.EnsureSuccessStatusCode();

        // LIVE review response is wrapped: { submission: {...} }. The product-
        // agnostic kyc-service does NOT name the Jeeb role on its review response
        // (verified live: an approve returns {submission:{id,subject,
        // status:"Verified",...}} with NO grantsRole on the envelope OR the
        // submission; a reject returns status:"Rejected"). That is correct by
        // ARCH LAW — only the gateway holds Jeeb vocabulary, kyc-service stays a
        // generic moderation primitive. So the gateway DERIVES the role-grant
        // INTENT from the outcome status: a review that lands on Verified is an
        // approve and grants the Jeeb (jeeber) role; any other terminal status
        // (Rejected / ResubmitRequested / unchanged) grants nothing. This mirrors
        // the submit path, which likewise stamps the Jeeb role in the gateway. We
        // still honor an explicit grantsRole if a future kyc-service wrapper emits
        // one (forward-compat), but never depend on it.
        var envelope = await ReadJsonAsync(response, ct);
        var submission = envelope.TryGetProperty("submission", out var sub)
            && sub.ValueKind == JsonValueKind.Object
                ? sub
                : envelope;
        var status = ReadString(submission, "status", "state") ?? throw EmptyId(response);
        var explicitGrant = ReadString(submission, "grantsRole")
            ?? ReadString(envelope, "grantsRole");
        // Derive the grant intent in the gateway: an approve is the only review
        // outcome that yields Verified, and it is the sole identity-mutating
        // transition (CP-C / H8). Empty for every non-approve outcome.
        var grantsRole = explicitGrant
            ?? (string.Equals(status, "Verified", StringComparison.OrdinalIgnoreCase)
                ? JeeberRole
                : null);

        return new KycReviewDecision
        {
            SubmissionId = ReadString(submission, "id", "submissionId") ?? submissionId,
            // The owning user the gateway appends the role to — LIVE field is
            // `subject`. Without it the gateway cannot complete the UM grant.
            UserId = ReadString(submission, "subject", "userId"),
            Status = status,
            RejectionReason = ReadString(submission, "rejectionReason", "reason"),
            ResubmitSteps = ReadStringList(submission, "resubmitSlots", "resubmitSteps"),
            GrantsRole = grantsRole,
        };
    }

    private static string ToWireAction(KycReviewActionKind action) => action switch
    {
        KycReviewActionKind.Approve => "approve",
        KycReviewActionKind.Reject => "reject",
        KycReviewActionKind.RequestResubmit => "request_resubmit",
        _ => throw new KycReviewValidationException($"Unsupported review action: {action}."),
    };

    // --- helpers -----------------------------------------------------------

    private async Task<KycSubmissionView?> GetSubmissionOrNullAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var doc = await ReadJsonAsync(response, ct);
        return MapSubmission(doc);
    }

    private static KycSubmissionView MapSubmission(JsonElement doc) => new()
    {
        SubmissionId = ReadString(doc, "id", "submissionId") ?? string.Empty,
        // LIVE kyc-service names the owning user `subject`.
        UserId = ReadString(doc, "subject", "userId") ?? string.Empty,
        Status = ReadString(doc, "status", "state") ?? string.Empty,
        SubmittedAt = ReadDate(doc, "submittedAt", "createdAt") ?? default,
        ReviewedAt = ReadDate(doc, "reviewedAt"),
        RejectionReason = ReadString(doc, "rejectionReason", "reason"),
        TosSignedAt = ReadDate(doc, "tosSignedAt", "tos_signed_at"),
        TosAcceptedVersion = ReadString(doc, "tosAcceptedVersion", "tos_accepted_version"),
        ResubmitSteps = ReadStringList(doc, "resubmitSlots", "resubmitSteps"),
        Document = doc,
    };

    private static void AddIdempotencyKey(HttpRequestMessage request, string idempotencyKey)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        // Clone so the JsonElement outlives the disposed JsonDocument.
        return doc.RootElement.Clone();
    }

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var doc = await ReadJsonAsync(response, ct);
            return ReadString(doc, "detail", "title");
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var i)
            ? i
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        JsonElement arr = default;
        var found = false;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out arr) && arr.ValueKind == JsonValueKind.Array)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }

    private static HttpRequestException EmptyId(HttpResponseMessage response) =>
        new($"kyc-service {response.RequestMessage?.RequestUri} returned a payload with no id/status.");

    // The Jeeb role-grant intent. The gateway owns Jeeb semantics (BR-1); it
    // tells kyc-service which opaque role an approve should signal, and composes
    // the actual user-management append itself (kyc-service never calls UM).
    private const string JeeberRole = "jeeber";

    // --- LIVE kyc-service wire DTOs (camelCase via JsonSerializerDefaults.Web) --
    // These mirror the deployed kyc-service OpenAPI contract at :10074 exactly.
    // The gateway-facing seam DTOs in IKycServiceClient stay richer; this is the
    // narrow on-the-wire shape so the request bodies deserialize server-side.

    private sealed class KycSubmitWire
    {
        public string? Subject { get; init; }
        public string? VehicleType { get; init; }
        public string? VehicleRegistration { get; init; }
        public string? IdFrontRef { get; init; }
        public string? IdBackRef { get; init; }
        public string? SelfieRef { get; init; }
        public string? GrantsRole { get; init; }
    }

    private sealed class KycTosSignatureWire
    {
        public string? SignatureBlob { get; init; }
        public string? AcceptedVersion { get; init; }
    }

    private sealed class KycReviewWire
    {
        public string? Action { get; init; }
        public string? Reason { get; init; }
        public IReadOnlyList<string>? ResubmitSlots { get; init; }
        public string? ReviewerId { get; init; }
    }
}
