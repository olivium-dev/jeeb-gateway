using System.Text;

namespace JeebGateway.Financials;

public sealed record EarningsStatementRequest(
    string JeeberId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string Language = "en");

public sealed record EarningsStatementResult(
    byte[] PdfBytes,
    string FileName,
    string ContentType);

public interface IEarningsPdfGenerator
{
    Task<EarningsStatementResult> GenerateAsync(
        EarningsStatementRequest request,
        CancellationToken ct);
}

/// <summary>
/// MVP: generates a simple text-based receipt. A production implementation
/// would use a PDF library (QuestPDF, iText, etc.) to produce a branded
/// statement with the Jeeb header, period breakdown, and QR verification.
/// </summary>
public sealed class SimpleEarningsPdfGenerator : IEarningsPdfGenerator
{
    private readonly IEarningsAggregationService _earnings;

    public SimpleEarningsPdfGenerator(IEarningsAggregationService earnings)
        => _earnings = earnings;

    public async Task<EarningsStatementResult> GenerateAsync(
        EarningsStatementRequest request,
        CancellationToken ct)
    {
        var summary = await _earnings.GetSummaryAsync(
            request.JeeberId, request.PeriodStart, request.PeriodEnd, ct);
        var daily = await _earnings.GetDailyBreakdownAsync(
            request.JeeberId, request.PeriodStart, request.PeriodEnd, ct);

        var sb = new StringBuilder();
        sb.AppendLine("JEEB EARNINGS STATEMENT");
        sb.AppendLine("========================");
        sb.AppendLine($"Jeeber ID: {summary.JeeberId}");
        sb.AppendLine($"Period: {summary.PeriodStart:yyyy-MM-dd} to {summary.PeriodEnd:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"Total Earnings:   {summary.TotalEarnings:N2} LBP");
        sb.AppendLine($"Commission:       {summary.TotalCommission:N2} LBP");
        sb.AppendLine($"Net Payout:       {summary.NetPayout:N2} LBP");
        sb.AppendLine($"Deliveries:       {summary.DeliveryCount}");
        sb.AppendLine();

        if (daily.Count > 0)
        {
            sb.AppendLine("Daily Breakdown:");
            sb.AppendLine("Date         | Gross     | Commission | Net       | Deliveries");
            sb.AppendLine("-------------|-----------|------------|-----------|----------");
            foreach (var d in daily)
            {
                sb.AppendLine($"{d.Date:yyyy-MM-dd}   | {d.Gross,9:N2} | {d.Commission,10:N2} | {d.Net,9:N2} | {d.Deliveries,10}");
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"earnings-{request.JeeberId}-{request.PeriodStart:yyyyMMdd}-{request.PeriodEnd:yyyyMMdd}.txt";

        return new EarningsStatementResult(bytes, fileName, "text/plain");
    }
}
