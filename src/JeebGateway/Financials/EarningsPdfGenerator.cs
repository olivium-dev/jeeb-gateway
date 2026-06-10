using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
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

// ── Configuration ─────────────────────────────────────────────────────────────

public sealed class EarningsStatementOptions
{
    public const string SectionName = "EarningsStatement";

    /// <summary>Signed URL expiry in hours (default 24 h).</summary>
    public int SignedUrlTtlHours { get; set; } = 24;

    /// <summary>HMAC key for signed URL tokens (base-64 encoded).</summary>
    public string SignedUrlHmacKey { get; set; } = Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));
}

// ── Signed-URL helpers ────────────────────────────────────────────────────────

public sealed record EarningsStatementLink(string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Generates and validates HMAC-SHA256 signed tokens for PDF statement URLs.
/// Token format: base64url(payload JSON) + "." + base64url(HMAC-SHA256)
/// </summary>
public sealed class EarningsStatementTokenService
{
    private readonly EarningsStatementOptions _opts;
    private readonly TimeProvider _clock;

    public EarningsStatementTokenService(
        IOptions<EarningsStatementOptions> opts,
        TimeProvider clock)
    {
        _opts  = opts.Value;
        _clock = clock;
    }

    public (string token, DateTimeOffset expiresAt) Create(
        string jeeberId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string lang)
    {
        var expiresAt = _clock.GetUtcNow().AddHours(_opts.SignedUrlTtlHours);
        var payload = new StatementTokenPayload(jeeberId, periodStart, periodEnd, lang, expiresAt);
        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadB64  = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var sig         = ComputeHmac(payloadB64);
        return ($"{payloadB64}.{sig}", expiresAt);
    }

    public (bool valid, StatementTokenPayload? payload) Validate(string token, string callerId)
    {
        var parts = token.Split('.');
        if (parts.Length != 2) return (false, null);

        var expectedSig = ComputeHmac(parts[0]);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(parts[1]),
            Encoding.UTF8.GetBytes(expectedSig)))
            return (false, null);

        StatementTokenPayload payload;
        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            payload = JsonSerializer.Deserialize<StatementTokenPayload>(json)!;
        }
        catch { return (false, null); }

        if (payload.ExpiresAt < _clock.GetUtcNow()) return (false, null);
        // If a Bearer token is present, its identity must match the signed-token's subject.
        // If no Bearer is present (callerId is empty), validate only token integrity + expiry.
        if (!string.IsNullOrEmpty(callerId) &&
            !string.Equals(payload.JeeberId, callerId, StringComparison.Ordinal))
            return (false, null);

        return (true, payload);
    }

    private string ComputeHmac(string data)
    {
        var key   = Convert.FromBase64String(_opts.SignedUrlHmacKey);
        var bytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        var pad  = (4 - s.Length % 4) % 4;
        var b64  = s.Replace('-', '+').Replace('_', '/') + new string('=', pad);
        return Convert.FromBase64String(b64);
    }
}

public sealed record StatementTokenPayload(
    string JeeberId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string Lang,
    DateTimeOffset ExpiresAt);

// ── Cache decorator ───────────────────────────────────────────────────────────

/// <summary>
/// IMemoryCache-backed decorator for <see cref="QuestPdfEarningsStatementGenerator"/>.
/// Cache key: <c>earnings-pdf:{jeeberId}:{periodStart:yyyyMMdd}:{periodEnd:yyyyMMdd}:{lang}</c>
/// TTL: 5 min (active period), 24 h (past/paid period).
/// </summary>
public sealed class CachedEarningsPdfGenerator : IEarningsPdfGenerator
{
    private readonly IEarningsPdfGenerator _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public CachedEarningsPdfGenerator(
        IEarningsPdfGenerator inner,
        IMemoryCache cache,
        TimeProvider clock)
    {
        _inner = inner;
        _cache = cache;
        _clock = clock;
    }

    public async Task<EarningsStatementResult> GenerateAsync(
        EarningsStatementRequest request,
        CancellationToken ct)
    {
        var key = BuildKey(request);
        if (_cache.TryGetValue(key, out byte[]? cached) && cached is { Length: > 0 })
        {
            return new EarningsStatementResult(cached, BuildFileName(request), "application/pdf");
        }

        var result = await _inner.GenerateAsync(request, ct);
        var ttl    = request.PeriodEnd < _clock.GetUtcNow()
            ? TimeSpan.FromHours(24)
            : TimeSpan.FromMinutes(5);

        _cache.Set(key, result.PdfBytes, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1,
        });

        return result;
    }

    public static string BuildKey(EarningsStatementRequest req) =>
        $"earnings-pdf:{req.JeeberId}:{req.PeriodStart:yyyyMMdd}:{req.PeriodEnd:yyyyMMdd}:{req.Language}";

    private static string BuildFileName(EarningsStatementRequest req) =>
        $"earnings-{req.JeeberId}-{req.PeriodStart:yyyyMMdd}-{req.PeriodEnd:yyyyMMdd}.pdf";
}

// ── QuestPDF generator ────────────────────────────────────────────────────────

/// <summary>
/// QuestPDF-backed earnings statement generator (S10 H6/A6, JEB-59).
///
/// Produces a real <c>application/pdf</c> document — Content-Type
/// <c>application/pdf</c> with the <c>%PDF</c> magic-byte header — for the
/// Jeeber's period statement. Supports bilingual (AR/EN) rendering.
///
/// JEB-59 additions:
/// - NotoSansArabic font embedded (registered once at startup via Program.cs)
/// - FontFamily("Noto Sans Arabic") applied for RTL layout
/// - Wrapped by <see cref="CachedEarningsPdfGenerator"/> in production
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
        var labels   = isArabic ? Labels.Arabic : Labels.English;

        var bytes = Render(projection, request, labels, isArabic);

        var fileName =
            $"earnings-{request.JeeberId}-{request.PeriodStart:yyyyMMdd}-{request.PeriodEnd:yyyyMMdd}.pdf";

        return new EarningsStatementResult(bytes, fileName, "application/pdf");
    }

    // Called by unit tests directly with a pre-built projection (no store I/O).
    internal static byte[] RenderDirect(
        EarningsProjection projection,
        EarningsStatementRequest request)
    {
        var isArabic = string.Equals(request.Language?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
        return Render(projection, request, isArabic ? Labels.Arabic : Labels.English, isArabic);
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

                if (rtl)
                {
                    page.ContentFromRightToLeft();
                    // JEB-59: use embedded NotoSansArabic for correct glyph shaping.
                    page.DefaultTextStyle(x => x.FontFamily("Noto Sans Arabic").FontSize(11));
                }
                else
                {
                    page.DefaultTextStyle(x => x.FontSize(11));
                }

                page.Header().Column(col =>
                {
                    col.Item().Text(l.Title).FontSize(18).Bold();
                    col.Item().Text($"{l.JeeberId}: {p.JeeberId}");
                    col.Item().Text(
                        $"{l.Period}: {p.PeriodStart:yyyy-MM-dd} \u2192 {p.PeriodEnd:yyyy-MM-dd}");
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
            Title: "\u0643\u0634\u0641 \u0623\u0631\u0628\u0627\u062d \u062c\u064a\u0628",
            JeeberId: "\u0645\u0639\u0631\u0651\u0641 \u0627\u0644\u062c\u064a\u0628\u0631",
            Period: "\u0627\u0644\u0641\u062a\u0631\u0629",
            Gross: "\u0627\u0644\u0625\u062c\u0645\u0627\u0644\u064a",
            Commission: "\u0627\u0644\u0639\u0645\u0648\u0644\u0629",
            Net: "\u0635\u0627\u0641\u064a \u0627\u0644\u0645\u0633\u062a\u062d\u0642",
            Deliveries: "\u0627\u0644\u062a\u0648\u0635\u064a\u0644\u0627\u062a",
            Breakdown: "\u0627\u0644\u062a\u0641\u0635\u064a\u0644",
            Delivery: "\u0627\u0644\u062a\u0648\u0635\u064a\u0644\u0629",
            Generated: "\u062a\u0627\u0631\u064a\u062e \u0627\u0644\u0625\u0635\u062f\u0627\u0631");
    }
}
