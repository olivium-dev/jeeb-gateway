using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

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
/// QuestPDF-backed earnings statement generator (S10 H6/A6, JEB-59).
///
/// Produces a real <c>application/pdf</c> document — Content-Type
/// <c>application/pdf</c> with the <c>%PDF</c> magic-byte header — for the
/// Jeeber's period statement, replacing the legacy text/plain stub. Supports a
/// bilingual header (English / Arabic) selected by the request language; the
/// numeric breakdown is locale-invariant.
///
/// The QuestPDF Community license is configured once at app startup
/// (Program.cs: <c>QuestPDF.Settings.License = LicenseType.Community</c>).
/// </summary>
public sealed class QuestPdfEarningsStatementGenerator : IEarningsPdfGenerator
{
    private readonly IEarningsAggregationService _earnings;

    public QuestPdfEarningsStatementGenerator(IEarningsAggregationService earnings)
        => _earnings = earnings;

    public async Task<EarningsStatementResult> GenerateAsync(
        EarningsStatementRequest request,
        CancellationToken ct)
    {
        var projection = await _earnings.GetProjectionAsync(
            request.JeeberId, request.PeriodStart, request.PeriodEnd, ct);

        var isArabic = string.Equals(request.Language?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
        var labels = isArabic ? Labels.Arabic : Labels.English;

        var bytes = Render(projection, request, labels, isArabic);

        var fileName =
            $"earnings-{request.JeeberId}-{request.PeriodStart:yyyyMMdd}-{request.PeriodEnd:yyyyMMdd}.pdf";

        return new EarningsStatementResult(bytes, fileName, "application/pdf");
    }

    private static byte[] Render(
        EarningsProjection p, EarningsStatementRequest req, Labels l, bool rtl)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(11));
                if (rtl)
                    page.ContentFromRightToLeft();

                page.Header().Column(col =>
                {
                    col.Item().Text(l.Title).FontSize(18).Bold();
                    col.Item().Text($"{l.JeeberId}: {p.JeeberId}");
                    col.Item().Text(
                        $"{l.Period}: {p.PeriodStart:yyyy-MM-dd} → {p.PeriodEnd:yyyy-MM-dd}");
                });

                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Item().Text($"{l.Gross}: {Money(p.Totals.Gross)} {p.Totals.Currency}");
                    col.Item().Text($"{l.Commission}: {Money(p.Totals.Commission)} {p.Totals.Currency}");
                    col.Item().Text($"{l.Net}: {Money(p.Totals.Net)} {p.Totals.Currency}").Bold();
                    col.Item().Text($"{l.Deliveries}: {p.DeliveryCount}");

                    if (p.Entries.Count > 0)
                    {
                        col.Item().PaddingTop(12).Text(l.Breakdown).Bold();
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(3); // delivery
                                c.RelativeColumn(2); // gross
                                c.RelativeColumn(2); // commission
                                c.RelativeColumn(2); // net
                            });

                            table.Header(h =>
                            {
                                h.Cell().Text(l.Delivery).Bold();
                                h.Cell().Text(l.Gross).Bold();
                                h.Cell().Text(l.Commission).Bold();
                                h.Cell().Text(l.Net).Bold();
                            });

                            foreach (var e in p.Entries)
                            {
                                table.Cell().Text(e.DeliveryId);
                                table.Cell().Text(Money(e.Gross));
                                table.Cell().Text(Money(e.Commission));
                                table.Cell().Text(Money(e.Net));
                            }
                        });
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span($"{l.Generated}: ");
                    t.Span(DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture));
                });
            });
        }).GeneratePdf();
    }

    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    private sealed record Labels(
        string Title, string JeeberId, string Period, string Gross, string Commission,
        string Net, string Deliveries, string Breakdown, string Delivery, string Generated)
    {
        public static readonly Labels English = new(
            Title: "JEEB EARNINGS STATEMENT",
            JeeberId: "Jeeber ID",
            Period: "Period",
            Gross: "Gross",
            Commission: "Commission",
            Net: "Net Payout",
            Deliveries: "Deliveries",
            Breakdown: "Breakdown",
            Delivery: "Delivery",
            Generated: "Generated");

        public static readonly Labels Arabic = new(
            Title: "كشف أرباح جيب",
            JeeberId: "معرّف الجيبر",
            Period: "الفترة",
            Gross: "الإجمالي",
            Commission: "العمولة",
            Net: "صافي المستحق",
            Deliveries: "التوصيلات",
            Breakdown: "التفصيل",
            Delivery: "التوصيلة",
            Generated: "تاريخ الإصدار");
    }
}
