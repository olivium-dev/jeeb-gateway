using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner.JeeberSearch;

/// <summary>
/// HTTP implementation of <see cref="IPartnerJeeberSearchClient"/> over the injected
/// <see cref="HttpClient"/> (bound to <c>UserManagementServiceApi:BaseUrl</c> with the
/// bearer-forwarding handler + Polly v8 resilience pipeline; wired in
/// <see cref="JeebGateway.Extensions.PartnerWalletExtensions"/>). Mirrors the hand-authored
/// <see cref="JeebGateway.Users.HttpUserManagementDualRoleClient"/>: web-default JSON, snake_case-
/// tolerant DTOs, and every non-success / transport / parse fault surfaced as
/// <see cref="PartnerJeeberSearchUpstreamException"/> (never a raw exception past the seam).
/// </summary>
public sealed class PartnerJeeberSearchClient : IPartnerJeeberSearchClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PartnerJeeberSearchClient> _log;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PartnerJeeberSearchClient(HttpClient http, ILogger<PartnerJeeberSearchClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<IReadOnlyList<PartnerJeeberSearchHit>> SearchJeebersAsync(
        string query, int limit, CancellationToken ct)
    {
        // GET api/User/jeebers/search?query={q}&limit={n} — the jeeber-scoped free-text search the
        // frozen PP-3 contract requires user-management to expose (name/phone match, phone in the
        // result). The "jeeber-type" filter is baked into the endpoint semantics so the gateway makes
        // NO assumption about user-management's opaque role vocabulary.
        var path = $"api/User/jeebers/search?query={Uri.EscapeDataString(query)}&limit={limit}";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(path, ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "user-management jeeber search transport failure");
            throw new PartnerJeeberSearchUpstreamException((int)HttpStatusCode.BadGateway, ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("user-management jeeber search returned {Status}", (int)resp.StatusCode);
                throw new PartnerJeeberSearchUpstreamException((int)resp.StatusCode);
            }

            UmJeeberSearchResponse? dto;
            try
            {
                dto = await resp.Content.ReadFromJsonAsync<UmJeeberSearchResponse>(Json, ct);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "user-management jeeber search returned an unreadable body");
                throw new PartnerJeeberSearchUpstreamException((int)HttpStatusCode.BadGateway, ex);
            }

            if (dto?.Users is not { Length: > 0 })
            {
                return Array.Empty<PartnerJeeberSearchHit>();
            }

            var hits = new List<PartnerJeeberSearchHit>(dto.Users.Length);
            foreach (var u in dto.Users)
            {
                if (string.IsNullOrWhiteSpace(u.UserId))
                {
                    continue; // a hit with no id is not a resolvable top-up target — drop it
                }

                var name = !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName!
                    : !string.IsNullOrWhiteSpace(u.Username) ? u.Username!
                    : string.Empty;

                hits.Add(new PartnerJeeberSearchHit(u.UserId!, name, u.Phone));
            }

            return hits;
        }
    }

    // ── wire DTOs (snake_case-tolerant, mirroring the sibling UM adapter) ─────────────────────────
    private sealed class UmJeeberSearchResponse
    {
        [JsonPropertyName("users")] public UmJeeberSearchUser[]? Users { get; set; }
    }

    private sealed class UmJeeberSearchUser
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("phone")] public string? Phone { get; set; }
    }
}
