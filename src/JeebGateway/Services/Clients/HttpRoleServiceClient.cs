using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed <see cref="IRoleServiceClient"/>. Wire is snake_case; DTOs
/// spell fields explicitly rather than a global naming policy.
/// </summary>
public sealed class HttpRoleServiceClient : IRoleServiceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<HttpRoleServiceClient> _log;

    public HttpRoleServiceClient(HttpClient http, ILogger<HttpRoleServiceClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<RoleServiceSubjectRoles> GetOrCreateAsync(string appId, string subjectId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(SubjectPath(appId, subjectId), ct);
        await ThrowIfError(resp, "get-or-create", ct);

        var dto = await resp.Content.ReadFromJsonAsync<SubjectDto>(Json, ct)
            ?? throw new RoleServiceCallException("get-or-create", (int)HttpStatusCode.BadGateway, null);
        return ToModel(dto);
    }

    public async Task<RoleServiceGrantResult> GrantAsync(
        string appId, string subjectId, string roleKey, string grantedBy,
        string idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, SubjectPath(appId, subjectId) + "/grant")
        {
            Content = JsonContent.Create(new GrantBody { RoleKey = roleKey, GrantedBy = grantedBy }, options: Json)
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);

        using var resp = await _http.SendAsync(req, ct);
        await ThrowIfError(resp, "grant", ct);

        // Mutations return a single-op body (RoleStore.GrantAsync), NOT the subject
        // envelope — re-read the authoritative subject view after success.
        var subject = await GetOrCreateAsync(appId, subjectId, ct);
        return new RoleServiceGrantResult(resp.StatusCode == HttpStatusCode.Created, subject);
    }

    public async Task<RoleServiceRevokeResult> RevokeAsync(
        string appId, string subjectId, string roleKey, string revokedBy,
        string? reassignActiveRoleTo, string idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, SubjectPath(appId, subjectId) + "/revoke")
        {
            Content = JsonContent.Create(
                new RevokeBody { RoleKey = roleKey, RevokedBy = revokedBy, ReassignActiveRoleTo = reassignActiveRoleTo },
                options: Json)
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);

        using var resp = await _http.SendAsync(req, ct);
        await ThrowIfError(resp, "revoke", ct);

        // Same single-op-body contract as grant: re-read the subject after success.
        return new RoleServiceRevokeResult(await GetOrCreateAsync(appId, subjectId, ct));
    }

    public async Task<RoleServiceActiveRoleResult> SetActiveRoleAsync(
        string appId, string subjectId, string roleKey, string setBy,
        string idempotencyKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, SubjectPath(appId, subjectId) + "/active-role")
        {
            Content = JsonContent.Create(new ActiveRoleBody { RoleKey = roleKey, SetBy = setBy }, options: Json)
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);

        using var resp = await _http.SendAsync(req, ct);
        await ThrowIfError(resp, "active-role", ct);

        // Same single-op-body contract as grant: re-read the subject after success.
        return new RoleServiceActiveRoleResult(await GetOrCreateAsync(appId, subjectId, ct));
    }

    public async Task<RoleServiceSubjectPage> ListByRoleAsync(
        string appId, string roleKey, string? status, string? cursor, int? limit, CancellationToken ct)
    {
        var q = new List<string> { $"role={Uri.EscapeDataString(roleKey)}" };
        if (!string.IsNullOrWhiteSpace(status)) q.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(cursor)) q.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (limit is > 0) q.Add($"limit={limit}");

        using var resp = await _http.GetAsync($"v1/roles/apps/{Uri.EscapeDataString(appId)}/subjects:query?{string.Join('&', q)}", ct);
        await ThrowIfError(resp, "list-by-role", ct);

        var dto = await resp.Content.ReadFromJsonAsync<SubjectPageDto>(Json, ct)
            ?? throw new RoleServiceCallException("list-by-role", (int)HttpStatusCode.BadGateway, null);

        var items = (dto.Items ?? Array.Empty<SubjectListItemDto>())
            .Select(i => new RoleServiceSubjectListItem(i.SubjectId ?? string.Empty, i.RoleKey ?? string.Empty, i.GrantedAt))
            .ToList();
        return new RoleServiceSubjectPage(items, dto.NextCursor);
    }

    private static string SubjectPath(string appId, string subjectId) =>
        $"v1/roles/apps/{Uri.EscapeDataString(appId)}/subjects/{Uri.EscapeDataString(subjectId)}";

    private static RoleServiceSubjectRoles ToModel(SubjectDto dto)
    {
        var roles = (dto.Roles ?? Array.Empty<RoleGrantDto>())
            .Select(r => new RoleServiceRoleGrant(r.RoleKey ?? string.Empty, r.Label, r.Scopes, r.GrantedBy, r.GrantedAt))
            .ToList();
        var active = dto.ActiveRole is null
            ? null
            : new RoleServiceActiveRole(dto.ActiveRole.RoleKey ?? string.Empty, dto.ActiveRole.SetBy, dto.ActiveRole.SetAt);
        return new RoleServiceSubjectRoles(dto.AppId ?? string.Empty, dto.SubjectId ?? string.Empty, roles, active);
    }

    private async Task ThrowIfError(HttpResponseMessage resp, string operation, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        string? errorCode = null;
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorDto>(Json, ct);
            // role-service errors are RFC7807 problem+json with type "urn:problem:<code>".
            errorCode = err?.Error ?? StripProblemPrefix(err?.Type);
        }
        catch (JsonException)
        {
            // Body wasn't the documented error shape; surface the bare status.
        }

        _log.LogWarning(
            "role-service '{Operation}' returned {Status} ({ErrorCode})",
            operation, (int)resp.StatusCode, errorCode ?? "unknown");
        throw new RoleServiceCallException(operation, (int)resp.StatusCode, errorCode);
    }

    // ---- wire DTOs (role-service SnakeCaseNamingPolicy) ----

    private sealed class GrantBody
    {
        [JsonPropertyName("role_key")] public string RoleKey { get; set; } = string.Empty;
        [JsonPropertyName("granted_by")] public string GrantedBy { get; set; } = string.Empty;
    }

    private sealed class RevokeBody
    {
        [JsonPropertyName("role_key")] public string RoleKey { get; set; } = string.Empty;
        [JsonPropertyName("revoked_by")] public string RevokedBy { get; set; } = string.Empty;
        [JsonPropertyName("reassign_active_role_to")] public string? ReassignActiveRoleTo { get; set; }
    }

    private sealed class ActiveRoleBody
    {
        [JsonPropertyName("role_key")] public string RoleKey { get; set; } = string.Empty;
        [JsonPropertyName("set_by")] public string SetBy { get; set; } = string.Empty;
    }

    private sealed class SubjectDto
    {
        [JsonPropertyName("app_id")] public string? AppId { get; set; }
        [JsonPropertyName("subject_id")] public string? SubjectId { get; set; }
        [JsonPropertyName("roles")] public RoleGrantDto[]? Roles { get; set; }
        [JsonPropertyName("active_role")] public ActiveRoleDto? ActiveRole { get; set; }
    }

    private sealed class RoleGrantDto
    {
        [JsonPropertyName("role_key")] public string? RoleKey { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("scopes")] public string[]? Scopes { get; set; }
        [JsonPropertyName("granted_by")] public string? GrantedBy { get; set; }
        [JsonPropertyName("granted_at")] public DateTimeOffset? GrantedAt { get; set; }
    }

    private sealed class ActiveRoleDto
    {
        [JsonPropertyName("role_key")] public string? RoleKey { get; set; }
        [JsonPropertyName("set_by")] public string? SetBy { get; set; }
        [JsonPropertyName("set_at")] public DateTimeOffset? SetAt { get; set; }
    }

    private sealed class SubjectListItemDto
    {
        [JsonPropertyName("subject_id")] public string? SubjectId { get; set; }
        [JsonPropertyName("role_key")] public string? RoleKey { get; set; }
        [JsonPropertyName("granted_at")] public DateTimeOffset? GrantedAt { get; set; }
    }

    private sealed class SubjectPageDto
    {
        [JsonPropertyName("items")] public SubjectListItemDto[]? Items { get; set; }
        [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
    }

    private static string? StripProblemPrefix(string? type) =>
        type is null ? null : (type.StartsWith("urn:problem:", StringComparison.Ordinal) ? type["urn:problem:".Length..] : type);

    private sealed class ErrorDto
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}
