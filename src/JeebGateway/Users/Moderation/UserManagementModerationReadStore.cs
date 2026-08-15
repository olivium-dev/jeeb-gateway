using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Migration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.Moderation;

/// <summary>gwdbx W4-07 — decorates the durable users projection so that at
/// dual-write-upstream-read+ every read's MODERATION state serves from user-management (W4-06).</summary>
public sealed class UserManagementModerationReadStore : IUserProjectionStore
{
    /// <summary>Named client configured with the user-management base address.</summary>
    public const string HttpClientName = "ModerationUpstreamRead";

    // W4-06 contract: POST api/User/moderation/query rejects >200 ids per call.
    public const int MaxIdsPerQuery = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IUserProjectionStore _inner;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<GwdbxMigrationOptions> _mode;
    private readonly ILogger<UserManagementModerationReadStore> _log;

    public UserManagementModerationReadStore(
        IUserProjectionStore inner,
        IHttpClientFactory http,
        IOptionsMonitor<GwdbxMigrationOptions> mode,
        ILogger<UserManagementModerationReadStore> log)
    {
        _inner = inner;
        _http = http;
        _mode = mode;
        _log = log;
    }

    private bool UpstreamReadActive =>
        _mode.CurrentValue.UserModeration >= GwdbxMigrationPhase.DualWriteUpstreamRead;

    public async Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        var row = await _inner.GetByIdAsync(userId, ct);
        if (row is null || !UpstreamReadActive)
        {
            return row;
        }

        await OverlayAsync(new[] { row }, ct);
        return row;
    }

    public async Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
    {
        var result = await _inner.SearchAsync(query, ct);
        if (!UpstreamReadActive || result.Items.Count == 0)
        {
            return result;
        }

        await OverlayAsync(result.Items, ct);
        return result;
    }

    // Identity writes and the purge stay local; the W4-04 mirror owns the upstream write leg.
    public Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct)
        => _inner.UpsertIdentityAsync(profile, ct);

    public Task SetSuspensionAsync(
        string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
        => _inner.SetSuspensionAsync(userId, isSuspended, reason, at, ct);

    public Task PurgePiiAsync(string userId, CancellationToken ct)
        => _inner.PurgePiiAsync(userId, ct);

    // Role counts are the identity domain, not moderation — always the local projection.
    public Task<UserRoleCounts> CountByRolesAsync(
        IReadOnlyCollection<string> opaqueRoles, CancellationToken ct)
        => _inner.CountByRolesAsync(opaqueRoles, ct);

    private async Task OverlayAsync(IReadOnlyList<UserProfile> rows, CancellationToken ct)
    {
        // Non-Guid ids are gateway-permissive artifacts UM can never hold; their local state stands.
        var byId = new Dictionary<Guid, List<UserProfile>>();
        foreach (var row in rows)
        {
            if (Guid.TryParse(row.Id, out var id))
            {
                if (!byId.TryGetValue(id, out var list))
                {
                    byId[id] = list = new List<UserProfile>();
                }
                list.Add(row);
            }
        }

        if (byId.Count == 0)
        {
            return;
        }

        var client = _http.CreateClient(HttpClientName);
        var ids = byId.Keys.ToList();
        for (var offset = 0; offset < ids.Count; offset += MaxIdsPerQuery)
        {
            var chunk = ids.Skip(offset).Take(MaxIdsPerQuery).ToList();

            // An upstream fault THROWS: silently serving local here would fake the cutover (W3-13).
            using var response = await client.PostAsJsonAsync(
                "api/User/moderation/query", new ModerationQueryWireRequest { UserIds = chunk }, Json, ct);
            response.EnsureSuccessStatusCode();
            var parsed = await response.Content.ReadFromJsonAsync<ModerationQueryWireResponse>(Json, ct)
                ?? throw new InvalidOperationException("user-management moderation query returned null");

            foreach (var (id, state) in parsed.States)
            {
                if (byId.TryGetValue(id, out var targets))
                {
                    foreach (var row in targets)
                    {
                        Apply(row, state);
                    }
                }
            }

            // Missing = a definitive upstream answer (id never projected), NOT a fault:
            // the local row stands and the gap is logged for the W4-05 backfill to close.
            foreach (var missing in parsed.Missing)
            {
                _log.LogWarning(
                    "moderation upstream read: userId={UserId} unknown upstream; serving local state.",
                    missing);
            }
        }
    }

    private static void Apply(UserProfile row, ModerationWireState state)
    {
        // Consumers key off isSuspended (W4-06 residual): metadata echoed on an
        // unsuspended row is stale and is canonicalized to null, matching the local shape.
        row.IsSuspended = state.IsSuspended;
        row.SuspensionReason = state.IsSuspended ? state.SuspensionReason : null;
        row.SuspendedAt = state.IsSuspended ? ToOffset(state.SuspendedAt) : null;
        row.SuspendedBy = state.IsSuspended ? state.SuspendedBy : null;
    }

    // UM serializes bare DateTime instants that were written as UTC (W4-03/W4-05).
    private static DateTimeOffset? ToOffset(DateTime? at)
        => at is null ? null : new DateTimeOffset(DateTime.SpecifyKind(at.Value, DateTimeKind.Utc));

    /// <summary>Wire shape of user-management POST api/User/moderation/query (W4-06).</summary>
    private sealed class ModerationQueryWireRequest
    {
        [JsonPropertyName("userIds")] public required List<Guid> UserIds { get; init; }
    }

    private sealed class ModerationQueryWireResponse
    {
        [JsonPropertyName("states")] public Dictionary<Guid, ModerationWireState> States { get; init; } = new();
        [JsonPropertyName("missing")] public List<Guid> Missing { get; init; } = new();
    }

    private sealed class ModerationWireState
    {
        [JsonPropertyName("isSuspended")] public bool IsSuspended { get; init; }
        [JsonPropertyName("suspensionReason")] public string? SuspensionReason { get; init; }
        [JsonPropertyName("suspendedAt")] public DateTime? SuspendedAt { get; init; }
        [JsonPropertyName("suspendedBy")] public string? SuspendedBy { get; init; }
    }
}
