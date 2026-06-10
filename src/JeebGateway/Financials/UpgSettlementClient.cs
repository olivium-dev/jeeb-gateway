using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials;

/// <summary>
/// Hand-coded interim transport for <see cref="IUpgSettlementClient"/> over the
/// UPG generic settlement endpoint. See the interface docs for the GR4 NSwag
/// regeneration plan (deferred to CI). Carries the org-standard bearer +
/// X-Service-Auth + resilience pipeline via its typed-client registration in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>.
/// </summary>
public sealed class UpgSettlementClient : IUpgSettlementClient
{
    private const string RecordPath = "api/v1/payments/settlements/record";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<UpgSettlementClient> _log;

    public UpgSettlementClient(HttpClient http, ILogger<UpgSettlementClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<UpgSettlementResponse> RecordSettlementAsync(UpgSettlementRequest request, CancellationToken ct)
    {
        if (_http.BaseAddress is null)
        {
            throw new InvalidOperationException(
                "UnifiedPayment BaseUrl is not configured; cannot post settlement to UPG.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["source"] = request.Source,
            ["externalRef"] = request.ExternalRef,
            ["payeeRef"] = request.PayeeRef,
            ["grossAmount"] = Money(request.GrossAmount),
            ["feeAmount"] = request.FeeAmount is { } fee ? Money(fee) : null,
            ["netAmount"] = request.NetAmount is { } net ? Money(net) : null,
            ["currency"] = request.Currency,
            ["metadata"] = request.Metadata,
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, RecordPath)
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        // UPG keys idempotency on (source, externalRef); also surface the natural
        // key as an Idempotency-Key for UPG's IdempotencyPlug.
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", $"{request.Source}:{request.ExternalRef}");

        using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "UPG settlement record failed: status={Status} source={Source} externalRef={ExternalRef} body={Body}",
                (int)resp.StatusCode, request.Source, request.ExternalRef, body);
            throw new HttpRequestException(
                $"UPG settlement record returned {(int)resp.StatusCode}.");
        }

        return Parse(body);
    }

    private static string Money(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static UpgSettlementResponse Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var id = data.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var status = data.TryGetProperty("status", out var st) ? st.GetString() : null;

        return new UpgSettlementResponse
        {
            SettlementId = id ?? string.Empty,
            Status = status,
        };
    }
}
