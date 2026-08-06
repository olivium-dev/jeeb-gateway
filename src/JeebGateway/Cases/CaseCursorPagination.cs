using System.Text.Json;

namespace JeebGateway.Cases;

public sealed record CaseCursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, int Total);

public static class CaseCursorPagination
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CaseCursorPage<GenericCaseV1> Cases(
        IReadOnlyList<GenericCaseV1> source, string? cursor, int limit,
        string scope = "support_cases")
    {
        var bounded = Math.Clamp(limit, 1, 100);
        var boundary = Decode(cursor, scope);
        var ordered = source.OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.CaseId)
            .Where(item => boundary is null || Before(item.CreatedAt, item.CaseId, boundary))
            .Take(bounded + 1)
            .ToArray();
        var hasMore = ordered.Length > bounded;
        var items = ordered.Take(bounded).ToArray();
        var next = hasMore && items.Length > 0
            ? Encode(scope, items[^1].CreatedAt, items[^1].CaseId)
            : null;
        return new CaseCursorPage<GenericCaseV1>(items, next, source.Count);
    }

    public static CaseCursorPage<GenericCaseMessageV1> Messages(
        IReadOnlyList<GenericCaseMessageV1> source, string? cursor, int limit)
    {
        var bounded = Math.Clamp(limit, 1, 100);
        var boundary = Decode(cursor, "support_messages");
        var ordered = source.Where(item => item.MessageType != "internal_note")
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.MessageId)
            .Where(item => boundary is null || Before(item.CreatedAt, item.MessageId, boundary))
            .Take(bounded + 1)
            .ToArray();
        var hasMore = ordered.Length > bounded;
        var selectedNewestFirst = ordered.Take(bounded).ToArray();
        var next = hasMore && selectedNewestFirst.Length > 0
            ? Encode("support_messages", selectedNewestFirst[^1].CreatedAt, selectedNewestFirst[^1].MessageId)
            : null;
        var items = selectedNewestFirst.Reverse().ToArray();
        var total = source.Count(item => item.MessageType != "internal_note");
        return new CaseCursorPage<GenericCaseMessageV1>(items, next, total);
    }

    private static bool Before(DateTimeOffset timestamp, Guid id, CursorBoundary boundary)
    {
        var ticks = timestamp.ToUniversalTime().Ticks;
        return ticks < boundary.Ticks || ticks == boundary.Ticks && id.CompareTo(boundary.Id) < 0;
    }

    private static string Encode(string scope, DateTimeOffset timestamp, Guid id)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new CursorBoundary(scope, timestamp.ToUniversalTime().Ticks, id), Json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static CursorBoundary? Decode(string? value, string expectedScope)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var base64 = value.Trim().Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var boundary = JsonSerializer.Deserialize<CursorBoundary>(Convert.FromBase64String(base64), Json);
            if (boundary is null || boundary.Scope != expectedScope || boundary.Id == Guid.Empty)
                throw new CaseValidationException("cursor is invalid for this case collection.");
            return boundary;
        }
        catch (CaseValidationException) { throw; }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new CaseValidationException("cursor is invalid for this case collection.");
        }
    }

    private sealed record CursorBoundary(string Scope, long Ticks, Guid Id);
}
