using System.Net.Http.Json;
using System.Text.Json;

namespace JeebGateway.Financials.Cod;

/// <summary>Stateless transport to the generic COD owner in unified-payment-gateway.</summary>
public sealed class HttpCodSettlementLedger : ICodSettlementLedger
{
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<HttpCodSettlementLedger> _logger;

    public HttpCodSettlementLedger(
        IHttpClientFactory clients,
        ILogger<HttpCodSettlementLedger> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public Task<CodLedgerResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "api/v1/payments/cod/record", request.DeliveryId, new
        {
            delivery_id = request.DeliveryId,
            provider_id = request.JeeberId,
            gross_amount = request.GrossAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commission_rate = request.CommissionRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            currency = request.Currency,
            metadata = request.Metadata,
        }, ct);

    public Task<CodLedgerResult> UpsertCodIntentAsync(CodIntentRequest request, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Put,
            $"api/v1/payments/cod/intents/{Uri.EscapeDataString(request.ExternalReference)}",
            $"cod-intent:{request.ExternalReference}:{request.SnapshotSequence}",
            new
            {
                providerId = request.ProviderId,
                grossAmount = Money(request.GrossAmount),
                commissionRate = Money(request.CommissionRate),
                currency = request.Currency,
                metadata = request.Metadata,
                snapshotSequence = request.SnapshotSequence,
            },
            ct);

    public Task<CodLedgerResult> FinalizeCodIntentAsync(
        CodFinalizeIntentRequest request, CancellationToken ct) =>
        SendAsync(
            HttpMethod.Post,
            $"api/v1/payments/cod/intents/{Uri.EscapeDataString(request.ExternalReference)}/finalize",
            request.IdempotencyKey,
            new
            {
                expectedVersion = request.ExpectedVersion,
                snapshotSequence = request.SnapshotSequence,
                grossAmount = Money(request.GrossAmount),
                commissionRate = Money(request.CommissionRate),
                providerId = request.ProviderId,
                currency = request.Currency,
                metadata = request.Metadata,
            },
            ct);

    public Task<CodLedgerResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get,
            $"api/v1/payments/cod/by-delivery/{Uri.EscapeDataString(deliveryId)}", null, null, ct);

    public Task<CodLedgerResult> MarkBatchPaidAsync(
        string batchId, string paidByAdminId, CancellationToken ct) =>
        Task.FromResult(new CodLedgerResult(true, StatusCodes.Status410Gone,
            JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "legacy_admin_action_retired",
                    message = "Use the versioned settlement-batch reconciliation endpoint.",
                }
            }), "application/json"));

    private async Task<CodLedgerResult> SendAsync(
        HttpMethod method, string relativePath, string? idempotencyKey, object? body, CancellationToken ct)
    {
        var client = _clients.CreateClient("admin-settlements");
        if (client.BaseAddress is null)
            return new CodLedgerResult(false, 0, null, "application/json");

        using var message = new HttpRequestMessage(method, relativePath);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null) message.Content = JsonContent.Create(body);

        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            return new CodLedgerResult(
                true,
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(ct),
                response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }
        catch (Exception error) when (
            error is HttpRequestException
            || error is TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(error, "COD owner call failed method={Method} path={Path}", method, relativePath);
            return new CodLedgerResult(false, 0, null, "application/json");
        }
    }

    private static string Money(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
