using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Clients;

/// <summary>
/// REALTIME PUBLISH AUTH CONTRACT — attaches a <c>live_comm</c> <b>publish</b>
/// token to every outbound realtime-comunication-service ingest call
/// (<c>POST /api/ingest/jeeb:chat/user:{recipientId}</c>) made by the typed
/// <see cref="RealtimeCommunicationClient"/>.
///
/// <para>
/// <b>Why this handler exists.</b> The org-standard outbound pipeline
/// (<see cref="JeebGateway.Services.Bff.BearerForwardingHandler"/> +
/// <see cref="JeebGateway.Services.Bff.ServiceAuthSigningHandler"/>) forwards
/// the inbound mobile JWT and an HMAC <c>X-Service-Auth</c> header. The realtime
/// service ("LiveComm", Elixir/Phoenix) authenticates ONLY a <c>live_comm</c>-issued
/// JWT (iss/aud <c>live_comm</c>) carrying <c>scopes</c>/<c>topics</c> claims that
/// its <c>ACL.authorize(claims, topic, :publish)</c> gate checks. The forwarded
/// mobile UM-JWT is signed with a different secret and lacks those claims, so the
/// ingest publish 401s — the exact blocker the chat live-push fix targets.
/// </para>
///
/// <para>
/// The minimal, additive reconciliation (mirroring the
/// <see cref="HeartBeatServiceAuthKeyHandler"/> precedent for heart-beat) is to
/// authenticate to realtime as a trusted publisher: mint a <c>live_comm</c>
/// token with <c>scopes:["publish"], topics:["jeeb:chat"]</c> via the service's
/// own minter (<c>POST /api/auth/token</c>) and set it as the <c>Authorization</c>
/// bearer, STRIPPING the inherited mobile bearer + HMAC header first (otherwise
/// the realtime service rejects the forwarded mobile JWT). The token is cached and
/// proactively refreshed before expiry, so the mint cost is paid roughly once per
/// token lifetime rather than per publish.
/// </para>
///
/// <para>
/// The handler is attached ONLY to the typed realtime client. It is a no-op
/// passthrough (no mint, headers left as-is) when realtime is not enabled
/// (<c>FeatureFlags:UseUpstream:Realtime</c> false) or no realtime BaseUrl is
/// configured — harmless because the gateway never dials realtime then.
/// </para>
/// </summary>
public sealed class RealtimePublishTokenHandler : DelegatingHandler
{
    private const string AuthHeader = "Authorization";
    private const string HmacHeaderName = "X-Service-Auth";

    /// <summary>The publisher identity stamped into the minted token's sub.</summary>
    private const string PublisherUserId = "jeeb-gateway-publisher";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IConfiguration _config;
    private readonly ILogger<RealtimePublishTokenHandler> _logger;

    // Cached publish token + its computed soft-expiry (refresh slightly early).
    private readonly SemaphoreSlim _mintLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RealtimePublishTokenHandler(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IConfiguration config,
        ILogger<RealtimePublishTokenHandler> logger)
    {
        _httpClientFactory = httpClientFactory;
        _flags = flags;
        _config = config;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Only act when realtime is actually wired. When off, leave the request
        // untouched — the gateway never reaches a live realtime host then.
        if (!_flags.CurrentValue.Realtime)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var token = await GetPublishTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            // The live_comm publish token is authoritative for realtime ingest.
            // Strip the inherited mobile bearer + fleet HMAC so the realtime ACL
            // evaluates OUR live_comm token (and never the mobile JWT it rejects).
            request.Headers.Remove(AuthHeader);
            request.Headers.Remove(HmacHeaderName);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string?> GetPublishTokenAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _cachedToken;
        if (cached is not null && now < _cachedUntil)
        {
            return cached;
        }

        await _mintLock.WaitAsync(ct);
        try
        {
            // Re-check under lock (another caller may have just minted).
            now = DateTimeOffset.UtcNow;
            if (_cachedToken is not null && now < _cachedUntil)
            {
                return _cachedToken;
            }

            var minted = await MintAsync(ct);
            if (minted is not null)
            {
                _cachedToken = minted.Value.Token;
                // Refresh 60s before the service-reported expiry; fall back to a
                // conservative 25-minute soft-TTL if the service omits expires_at.
                var hardExpiry = minted.Value.ExpiresAt ?? now.AddMinutes(30);
                var soft = hardExpiry.AddSeconds(-60);
                _cachedUntil = soft > now ? soft : now.AddMinutes(25);
            }

            return _cachedToken;
        }
        finally
        {
            _mintLock.Release();
        }
    }

    private async Task<(string Token, DateTimeOffset? ExpiresAt)?> MintAsync(CancellationToken ct)
    {
        var baseUrl = _config["Services:Realtime:BaseUrl"] ?? _config["Services:Realtime"];
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("PORT_TBD", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Realtime publish token: Services:Realtime BaseUrl is unset/placeholder; skipping mint.");
            return null;
        }

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        // A bare HttpClient (no BFF handler chain) so the mint call itself does NOT
        // forward the mobile bearer / HMAC — it is an unauthenticated dev-minter call.
        var http = _httpClientFactory.CreateClient("realtime-token-mint");
        http.BaseAddress = new Uri(baseUrl);

        var body = new MintRequest
        {
            UserId = PublisherUserId,
            Role = "service",
            Scopes = new[] { "publish" },
            Topics = new[] { RealtimeCommunicationClient.ChatTopic },
        };

        try
        {
            using var resp = await http.PostAsJsonAsync("api/auth/token", body, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Realtime publish token mint returned {Status}.", (int)resp.StatusCode);
                return null;
            }

            var minted = await resp.Content.ReadFromJsonAsync<MintResponse>(JsonOptions, ct);
            if (minted is null || string.IsNullOrWhiteSpace(minted.Token))
            {
                _logger.LogWarning("Realtime publish token mint returned an empty token.");
                return null;
            }

            return (minted.Token, minted.ExpiresAtOffset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realtime publish token mint failed.");
            return null;
        }
    }

    private sealed class MintRequest
    {
        [JsonPropertyName("user_id")]
        public string UserId { get; init; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; init; } = "service";

        [JsonPropertyName("scopes")]
        public string[] Scopes { get; init; } = System.Array.Empty<string>();

        [JsonPropertyName("topics")]
        public string[] Topics { get; init; } = System.Array.Empty<string>();
    }

    private sealed class MintResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; init; }

        // The LiveComm minter returns expires_at as a unix epoch (seconds). Tolerate
        // both a numeric epoch and an ISO string so a contract tweak doesn't break us.
        [JsonPropertyName("expires_at")]
        public JsonElement? ExpiresAt { get; init; }

        public DateTimeOffset? ExpiresAtOffset
        {
            get
            {
                if (ExpiresAt is null)
                {
                    return null;
                }

                var el = ExpiresAt.Value;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var epoch))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(epoch);
                }

                if (el.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(el.GetString(), out var parsed))
                {
                    return parsed;
                }

                return null;
            }
        }
    }
}
