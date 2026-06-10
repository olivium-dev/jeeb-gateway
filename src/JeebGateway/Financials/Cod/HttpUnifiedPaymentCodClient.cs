using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials.Cod;

/// <summary>
/// HTTP-backed <see cref="IUnifiedPaymentCodClient"/> dialing UPG (port 10066).
///
/// Auth composition (UPG pipelines, router.ex):
///   * <c>:api</c> pipeline (COD record/read) — <c>X-Api-Key</c> = the configured
///     UPG api key. The gateway forwards the user's identity in metadata only;
///     the wire credential to UPG is the api key (the user JWT is NOT a UPG
///     credential).
///   * <c>AdminAuthPlug</c> (mark-paid) — <c>Authorization: Bearer {admin_api_key}</c>
///     + <c>X-Admin-Id: {paidBy}</c>. The gateway authorizes the admin USER JWT
///     at its OWN boundary first; <paramref name="paidByAdminId"/> is the
///     authenticated principal id (never a client-supplied header — closes E12).
///
/// Returns UPG's status + body VERBATIM so the composing controller can re-emit
/// the upstream contract without reshaping it.
/// </summary>
public sealed class HttpUnifiedPaymentCodClient : IUnifiedPaymentCodClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly UnifiedPaymentCodOptions _options;
    private readonly ILogger<HttpUnifiedPaymentCodClient> _log;

    public HttpUnifiedPaymentCodClient(
        HttpClient http,
        IOptions<UnifiedPaymentCodOptions> options,
        ILogger<HttpUnifiedPaymentCodClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public Task<UpgResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct)
    {
        var payload = new
        {
            delivery_id = request.DeliveryId,
            provider_id = request.JeeberId,
            jeeber_id = request.JeeberId,
            gross_amount = request.GrossAmount,
            commission_rate = request.CommissionRate,
            commission_amount = request.CommissionAmount,
            currency = request.Currency,
            metadata = request.Metadata,
        };

        return SendAsync(HttpMethod.Post, "api/v1/payments/cod/record", apiKey: true, adminId: null,
            // delivery_id is UPG's natural idempotency key; also send the header so
            // the UPG IdempotencyPlug replays a duplicate record instead of erroring.
            idempotencyKey: request.DeliveryId,
            body: payload, ct);
    }

    public Task<UpgResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct)
        => SendAsync(HttpMethod.Get,
            $"api/v1/payments/cod_jeeb/by-delivery/{Uri.EscapeDataString(deliveryId)}",
            apiKey: true, adminId: null, idempotencyKey: null, body: null, ct);

    public Task<UpgResult> MarkBatchPaidAsync(string batchId, string paidByAdminId, CancellationToken ct)
        => SendAsync(HttpMethod.Post,
            $"admin/v1/settlements/{Uri.EscapeDataString(batchId)}/mark-paid",
            apiKey: false, adminId: paidByAdminId, idempotencyKey: null,
            body: new { event_type = "settlement.paid" }, ct);

    private async Task<UpgResult> SendAsync(
        HttpMethod method, string relativePath, bool apiKey, string? adminId,
        string? idempotencyKey, object? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, relativePath);

        if (apiKey && !string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.ApiKey);

        if (adminId is not null)
        {
            if (!string.IsNullOrWhiteSpace(_options.AdminApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AdminApiKey);
            message.Headers.TryAddWithoutValidation("X-Admin-Id", adminId);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        if (body is not null)
            message.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            return new UpgResult((int)response.StatusCode is > 0, (int)response.StatusCode,
                string.IsNullOrEmpty(text) ? null : text, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "UPG COD call {Method} {Path} unreachable", method, relativePath);
            return UpgResult.Unreachable();
        }
    }
}

/// <summary>
/// In-memory <see cref="IUnifiedPaymentCodClient"/> fallback for dev/test when
/// <c>Services:UnifiedPayment:BaseUrl</c> is not configured. Records the COD
/// intent in-process (idempotent on delivery id) and fronts a synthetic batch so
/// the compose surface is exercisable without a live UPG. The live path swaps to
/// <see cref="HttpUnifiedPaymentCodClient"/> when the BaseUrl is present.
/// </summary>
public sealed class InMemoryUnifiedPaymentCodClient : IUnifiedPaymentCodClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _byDelivery = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _batchStatus = new(StringComparer.Ordinal);

    public Task<UpgResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct)
    {
        var batchId = $"batch-{request.JeeberId}";
        var record = new
        {
            delivery_id = request.DeliveryId,
            provider_id = request.JeeberId,
            jeeber_id = request.JeeberId,
            gross_amount = request.GrossAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commission_amount = request.CommissionAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            currency = request.Currency,
            status = "batched",
            batchId,
        };
        _byDelivery[request.DeliveryId] = record;
        _batchStatus.TryAdd(batchId, "ready_to_pay");
        return Json(StatusCodes.Status201Created, new { data = record });
    }

    public Task<UpgResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        if (_byDelivery.TryGetValue(deliveryId, out var record))
            return Json(StatusCodes.Status200OK, record);
        return Json(StatusCodes.Status404NotFound, new { error = "not_found" });
    }

    public Task<UpgResult> MarkBatchPaidAsync(string batchId, string paidByAdminId, CancellationToken ct)
    {
        if (!_batchStatus.TryGetValue(batchId, out var status))
            return Json(StatusCodes.Status404NotFound, new { error = "not_found" });
        if (string.Equals(status, "paid", StringComparison.Ordinal))
            return Json(StatusCodes.Status409Conflict, new { error = "already_paid" });
        if (string.Equals(status, "cancelled", StringComparison.Ordinal))
            return Json(StatusCodes.Status422UnprocessableEntity, new { error = "terminal_non_payable" });

        _batchStatus[batchId] = "paid";
        return Json(StatusCodes.Status200OK, new
        {
            status = "paid",
            paidBy = paidByAdminId,
            paidAt = DateTimeOffset.UtcNow,
            batchId,
        });
    }

    private static Task<UpgResult> Json(int status, object body) =>
        Task.FromResult(new UpgResult(true, status,
            JsonSerializer.Serialize(body, JsonOptions), "application/json"));
}

/// <summary>UPG COD/admin credentials. Env-injected; never committed.</summary>
public sealed class UnifiedPaymentCodOptions
{
    public const string SectionName = "Services:UnifiedPayment";

    /// <summary>UPG :api pipeline api key (ApiKeyPlug). Header name defaults to X-Api-Key.</summary>
    public string? ApiKey { get; init; }

    public string ApiKeyHeader { get; init; } = "X-Api-Key";

    /// <summary>UPG AdminAuthPlug bearer secret (admin_api_key). Sent as Authorization: Bearer.</summary>
    public string? AdminApiKey { get; init; }
}
