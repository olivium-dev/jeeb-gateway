using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// gwdbx W3-02 — the READ half of the OTP-escalation leg: a stateless projection over
/// delivery-service <c>/api/v1/escalations</c>, selected by <c>FeatureFlags:OtpEscalationsMode</c>
/// from <c>dual-write-upstream-read</c> up.
///
/// <para><b>One upstream.</b> Deliberately delivery-service — the same service, route and wire
/// shape <see cref="DeliveryServiceEscalationMirror"/> already writes to, so the mirror and the
/// read can never point at different owners. (<c>StateServiceAdminEscalationStore</c> targets the
/// state-service CASE engine instead; it is not on this leg's path.)</para>
///
/// <para><b>No local fallback.</b> Every method fails CLOSED on an upstream fault. The local
/// admin_escalations store is in-memory, so a silent fallback would show an admin an empty
/// escalation queue during an outage and invite a "nothing to triage" conclusion.</para>
///
/// <para><b>Contract expected of delivery-service</b> (confirm before the read flip):
/// <c>POST api/v1/escalations</c> (idempotent on <c>escalation_id</c>) and
/// <c>GET api/v1/escalations[?delivery_id=]</c> returning either a bare array or
/// <c>{"items":[...]}</c> of the same row shape.</para>
/// </summary>
public sealed class DeliveryServiceAdminEscalationStore : IAdminEscalationStore
{
    /// <summary>Named client configured with the delivery-service base address.</summary>
    public const string HttpClientName = EscalationMirrorDrainer.HttpClientName;

    private const string Route = "api/v1/escalations";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<DeliveryServiceAdminEscalationStore> _log;

    public DeliveryServiceAdminEscalationStore(
        IHttpClientFactory http, ILogger<DeliveryServiceAdminEscalationStore> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<AdminEscalation> CreateAsync(AdminEscalation entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Synchronous on this path, unlike the fire-and-forget mirror: at the read rung the row is
        // only real once upstream has it, so a queued write would hand back a phantom escalation.
        var client = _http.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(Route, ToWire(entry), Json, ct);

        // 200 = idempotent replay of a row already written; both are success (G-15 on escalation_id).
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            throw new HttpRequestException(
                $"delivery-service {Route} returned {(int)response.StatusCode} for escalationId={entry.Id}.",
                null,
                response.StatusCode);
        }

        var row = await ReadRowAsync(response, ct);
        return row is null ? entry : Map(row);
    }

    public async Task<AdminEscalation?> GetForDeliveryAsync(
        string deliveryId, string reason, CancellationToken ct)
    {
        var rows = await ListRowsAsync($"{Route}?delivery_id={Uri.EscapeDataString(deliveryId)}", ct);
        return rows
            .Where(row => string.Equals(row.DeliveryId, deliveryId, StringComparison.Ordinal)
                          && string.Equals(row.Reason, reason, StringComparison.Ordinal))
            .Select(Map)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AdminEscalation>> ListAsync(CancellationToken ct)
    {
        var rows = await ListRowsAsync(Route, ct);
        return rows.OrderBy(row => row.CreatedAt).Select(Map).ToArray();
    }

    private async Task<IReadOnlyList<EscalationRow>> ListRowsAsync(string path, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        using var response = await client.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "escalations read: delivery-service {Path} returned {Status}.", path, (int)response.StatusCode);
            throw new HttpRequestException(
                $"delivery-service {path} returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return Array.Empty<EscalationRow>();

        using var document = JsonDocument.Parse(payload);
        var element = document.RootElement.ValueKind == JsonValueKind.Object
                      && document.RootElement.TryGetProperty("items", out var items)
            ? items
            : document.RootElement;
        return element.ValueKind == JsonValueKind.Array
            ? element.Deserialize<List<EscalationRow>>(Json) ?? new List<EscalationRow>()
            : Array.Empty<EscalationRow>();
    }

    private static async Task<EscalationRow?> ReadRowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<EscalationRow>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EscalationMirrorRequest ToWire(AdminEscalation entry) => new()
    {
        EscalationId = entry.Id,
        DeliveryId = entry.DeliveryId,
        ClientId = entry.ClientId,
        // G-28 — holder-generic wire name; the gateway's jeeberId is the provider.
        ProviderId = entry.JeeberId,
        Reason = entry.Reason,
        Status = entry.Status,
        AttemptCount = entry.OtpAttemptCount,
        CreatedAt = entry.CreatedAt,
    };

    private static AdminEscalation Map(EscalationRow row) => new()
    {
        Id = row.EscalationId,
        DeliveryId = row.DeliveryId,
        ClientId = row.ClientId,
        JeeberId = row.ProviderId,
        Reason = row.Reason,
        Status = row.Status,
        CreatedAt = row.CreatedAt,
        OtpAttemptCount = row.AttemptCount,
    };

    /// <summary>Read shape of delivery-service escalations (mirrors <see cref="EscalationMirrorRequest"/>).</summary>
    private sealed class EscalationRow
    {
        [JsonPropertyName("escalation_id")] public string EscalationId { get; set; } = string.Empty;
        [JsonPropertyName("delivery_id")] public string DeliveryId { get; set; } = string.Empty;
        [JsonPropertyName("client_id")] public string ClientId { get; set; } = string.Empty;
        [JsonPropertyName("provider_id")] public string? ProviderId { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("attempt_count")] public int AttemptCount { get; set; }
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    }
}
