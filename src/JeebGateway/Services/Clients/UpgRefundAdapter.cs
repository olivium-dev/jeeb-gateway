using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Services.Generated.UnifiedPaymentGateway;
using UpgRefundRequestDto = JeebGateway.Services.Generated.UnifiedPaymentGateway.RefundRequest;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Adapts the NSwag-generated <see cref="IServiceUnifiedPaymentGatewayClient"/>
/// (JEB-1503) to the narrow <see cref="IPaymentRefundClient"/> interface consumed
/// by the dispute orchestrator (T-BE-028 / JEB-64).
///
/// Replaces the hand-coded <see cref="HttpPaymentRefundClient"/>.
///
/// Per-call idempotency: the typed client does not expose per-call request headers,
/// so this adapter sends the raw HttpRequest via the same named HttpClient
/// ("UnifiedPaymentGateway") to attach the Idempotency-Key header on each call.
/// UPG deduplicates by payment-id + Idempotency-Key so retrying a dispute resolve
/// never double-refunds. The dispute orchestrator supplies
/// "dispute:{caseId}:refund" as the key.
/// </summary>
internal sealed class UpgRefundAdapter : IPaymentRefundClient
{
    internal const string HttpClientName = "UnifiedPaymentGateway";

    private static readonly JsonSerializerOptions _jsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _factory;

    public UpgRefundAdapter(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct)
    {
        try
        {
            using var http = _factory.CreateClient(HttpClientName);
            using var msg = new HttpRequestMessage(
                HttpMethod.Post,
                $"api/v1/payments/{Uri.EscapeDataString(request.DeliveryId)}/refund")
            {
                Content = JsonContent.Create(
                    new UpgRefundRequestDto
                    {
                        Amount = (double)request.AmountUsd,
                        Reason = request.Reason
                    },
                    options: _jsonOptions)
            };
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);

            using var response = await http.SendAsync(msg, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new RefundResult
                {
                    Success       = false,
                    FailureReason = $"UPG returned {(int)response.StatusCode}"
                };
            }

            var payload = await response.Content.ReadFromJsonAsync<RefundResponse>(_jsonOptions, ct);
            return new RefundResult
            {
                Success       = true,
                LedgerEntryId = payload?.Data.Id
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RefundResult
            {
                Success       = false,
                FailureReason = ex.Message
            };
        }
    }
}
