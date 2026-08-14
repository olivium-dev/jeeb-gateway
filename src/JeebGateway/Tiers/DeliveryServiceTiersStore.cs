using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Tiers;

/// <summary>
/// gwdbx W4-09 — <see cref="ITiersStore"/> served by delivery-service's durable
/// tier catalog (W4-08). Registered as the store only at
/// <c>FeatureFlags:TiersMode=upstream-authority</c> (freeze-import-flip: no
/// dual-write rung exists, and the W4-10 backfill imports the gateway catalog
/// upstream during the O4/A14 authoring freeze BEFORE the flip).
///
/// <para><b>Reads (W4-11 contract).</b> One upstream <c>GET /api/v1/tiers</c>
/// snapshot cached for 60s; on an upstream failure the store FAILS OPEN to the
/// last-known snapshot (stale catalog beats a dead create hot path). Only a
/// cold start with no snapshot ever throws.</para>
///
/// <para><b>Identity.</b> The gateway tier id maps to the upstream <c>code</c>
/// (the human slug — urgent/same-day/scheduled after W4-10), NOT the upstream
/// UUID, so tier ids embedded in existing requests keep resolving unchanged.</para>
/// </summary>
public sealed class DeliveryServiceTiersStore : ITiersStore
{
    /// <summary>Named client configured with the delivery-service base address.</summary>
    public const string HttpClientName = "TiersUpstream";

    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<DeliveryServiceTiersStore> _log;
    private readonly TimeProvider _clock;
    private readonly object _cacheLock = new();

    private IReadOnlyList<DeliveryTier>? _snapshot;
    private DateTimeOffset _snapshotAt;

    public DeliveryServiceTiersStore(
        IHttpClientFactory http,
        ILogger<DeliveryServiceTiersStore> log,
        TimeProvider? clock = null)
    {
        _http = http;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<DeliveryTier>> ListAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_snapshot is not null && _clock.GetUtcNow() - _snapshotAt < CacheTtl)
            {
                return _snapshot;
            }
        }

        try
        {
            var fresh = await FetchAsync(ct);
            lock (_cacheLock)
            {
                _snapshot = fresh;
                _snapshotAt = _clock.GetUtcNow();
            }
            return fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IReadOnlyList<DeliveryTier>? stale;
            lock (_cacheLock)
            {
                stale = _snapshot;
            }
            if (stale is not null)
            {
                // W4-11: fail open to last-known — the create hot path must not
                // die with delivery-service; staleness is bounded by real edits.
                _log.LogWarning(ex,
                    "upstream tier catalog fetch failed; serving last-known snapshot from {At}",
                    _snapshotAt);
                return stale;
            }
            throw;
        }
    }

    public async Task<DeliveryTier?> GetAsync(string id, CancellationToken ct)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault(t =>
            string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DeliveryTier> CreateAsync(
        DeliveryTierCreate input, string adminUserId, CancellationToken ct)
    {
        var code = NormalizeCode(input.Id) ?? SlugFromName(input.Name);
        var existing = await GetAsync(code, ct);
        if (existing is not null)
        {
            throw new DuplicateTierIdException(code);
        }

        await UpsertUpstreamAsync(code, ToUpsert(code, input.Name, input.SlaHours, input.RadiusKm,
            input.RequestTtlSeconds, input.CommissionRate, input.PriceHint), adminUserId, ct);
        return await RefetchRequiredAsync(code, ct);
    }

    public async Task<DeliveryTier?> ReplaceAsync(
        string id, DeliveryTierReplace input, string adminUserId, CancellationToken ct)
    {
        var code = NormalizeCode(id)!;
        var existing = await GetAsync(code, ct);
        if (existing is null)
        {
            return null;
        }

        await UpsertUpstreamAsync(code, ToUpsert(code, input.Name, input.SlaHours, input.RadiusKm,
            input.RequestTtlSeconds, input.CommissionRate, input.PriceHint), adminUserId, ct);
        return await RefetchRequiredAsync(code, ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"api/v1/admin/tiers/{Uri.EscapeDataString(NormalizeCode(id)!)}");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        Invalidate();
        return true;
    }

    // ---- internals ---------------------------------------------------------

    private void Invalidate()
    {
        lock (_cacheLock)
        {
            _snapshot = null;
        }
    }

    private async Task<IReadOnlyList<DeliveryTier>> FetchAsync(CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        var rows = await client.GetFromJsonAsync<List<UpstreamTier>>("api/v1/tiers", Json, ct)
            ?? throw new InvalidOperationException("upstream tier catalog returned null");
        return rows.Select(Map).ToList();
    }

    private async Task<DeliveryTier> RefetchRequiredAsync(string code, CancellationToken ct)
    {
        Invalidate();
        return await GetAsync(code, ct)
            ?? throw new InvalidOperationException(
                $"tier '{code}' vanished from the upstream catalog right after its upsert");
    }

    private async Task UpsertUpstreamAsync(
        string code, UpstreamTierUpsert body, string adminUserId, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"api/v1/admin/tiers/{Uri.EscapeDataString(code)}")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        if (!string.IsNullOrWhiteSpace(adminUserId))
        {
            request.Headers.TryAddWithoutValidation("X-Actor-Ref", adminUserId);
        }
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static UpstreamTierUpsert ToUpsert(
        string code, string name, int slaHours, double radiusKm,
        int requestTtlSeconds, double commissionRate, string priceHint) => new()
    {
        Code = code,
        DisplayNameEn = name,
        TaglineEn = priceHint,
        // The gateway catalog has no ttl_minutes concept; the SLA window is it.
        TtlMinutes = Math.Max(1, slaHours) * 60,
        RadiusKm = Math.Max(1, (int)Math.Round(radiusKm)),
        MaxProviders = 25,
        PricingMultiplier = 1.0,
        CommissionRate = commissionRate,
        PriceHint = priceHint,
        RequestTtlSeconds = requestTtlSeconds,
        SlaHours = slaHours,
    };

    private static string? NormalizeCode(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

    private static string SlugFromName(string name)
        => string.Join("-",
                name.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            is { Length: > 0 } slug
            ? slug
            : throw new ArgumentException("tier name must be non-empty", nameof(name));

    private static DeliveryTier Map(UpstreamTier t) => new()
    {
        // code, not UUID: existing requests carry slug tier ids (class doc).
        Id = string.IsNullOrWhiteSpace(t.Code) ? t.Id : t.Code,
        Name = t.Name ?? t.Code,
        SlaHours = t.SlaHours,
        RadiusKm = t.RadiusKmFloat > 0 ? t.RadiusKmFloat : t.RadiusKm,
        RequestTtlSeconds = t.RequestTtlSeconds > 0
            ? t.RequestTtlSeconds
            : t.TtlSeconds > 0 ? t.TtlSeconds : checked(t.TtlMinutes * 60),
        CommissionRate = t.CommissionRate,
        PriceHint = t.PriceHint ?? string.Empty,
        CreatedAt = t.CreatedAt ?? DateTimeOffset.UnixEpoch,
        UpdatedAt = t.UpdatedAt ?? DateTimeOffset.UnixEpoch,
    };

    /// <summary>Wire shape of delivery-service GET /api/v1/tiers (additive W4-08 keys).</summary>
    private sealed class UpstreamTier
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("code")] public string Code { get; init; } = "";
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("slaHours")] public int SlaHours { get; init; }
        [JsonPropertyName("radius_km")] public int RadiusKm { get; init; }
        [JsonPropertyName("radiusKm")] public double RadiusKmFloat { get; init; }
        [JsonPropertyName("ttl_minutes")] public int TtlMinutes { get; init; }
        [JsonPropertyName("ttl_seconds")] public int TtlSeconds { get; init; }
        [JsonPropertyName("request_ttl_seconds")] public int RequestTtlSeconds { get; init; }
        [JsonPropertyName("commissionRate")] public double CommissionRate { get; init; }
        [JsonPropertyName("priceHint")] public string? PriceHint { get; init; }
        [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; init; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; init; }
    }

    /// <summary>Wire shape of delivery-service PUT /api/v1/admin/tiers/{code} (W4-08).</summary>
    private sealed class UpstreamTierUpsert
    {
        [JsonPropertyName("code")] public required string Code { get; init; }
        [JsonPropertyName("display_name_en")] public required string DisplayNameEn { get; init; }
        [JsonPropertyName("tagline_en")] public string? TaglineEn { get; init; }
        [JsonPropertyName("ttl_minutes")] public int TtlMinutes { get; init; }
        [JsonPropertyName("radius_km")] public int RadiusKm { get; init; }
        [JsonPropertyName("max_providers")] public int MaxProviders { get; init; }
        [JsonPropertyName("pricing_multiplier")] public double PricingMultiplier { get; init; }
        [JsonPropertyName("commission_rate")] public double CommissionRate { get; init; }
        [JsonPropertyName("price_hint")] public string? PriceHint { get; init; }
        [JsonPropertyName("request_ttl_seconds")] public int RequestTtlSeconds { get; init; }
        [JsonPropertyName("sla_hours")] public int SlaHours { get; init; }
    }
}
