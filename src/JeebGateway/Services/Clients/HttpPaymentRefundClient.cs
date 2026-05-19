using System.Net.Http.Json;
using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed <see cref="IPaymentRefundClient"/>. Lights up when
/// <c>Services:UnifiedPayment:BaseUrl</c> is configured.
///
/// Calls POST /v1/refunds on <c>unified_payment_gateway</c> with the
/// case id as the <c>Idempotency-Key</c> so retries from the gateway
/// never double-post.
/// </summary>
public sealed class HttpPaymentRefundClient : IPaymentRefundClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HttpPaymentRefundClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/refunds")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.Add("Idempotency-Key", request.IdempotencyKey);

        try
        {
            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new RefundResult
                {
                    Success = false,
                    FailureReason = $"upstream returned {(int)response.StatusCode}"
                };
            }

            var payload = await response.Content.ReadFromJsonAsync<RefundResponseDto>(JsonOptions, ct);
            return new RefundResult
            {
                Success = true,
                LedgerEntryId = payload?.LedgerEntryId
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RefundResult
            {
                Success = false,
                FailureReason = ex.Message
            };
        }
    }

    private sealed record RefundResponseDto(string LedgerEntryId, DateTimeOffset? PostedAt);
}
