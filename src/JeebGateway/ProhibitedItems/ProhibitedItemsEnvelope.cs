using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.ProhibitedItems;

// gwdbx W3-03 — the lexicon's config-surface payload. ONE place so the importer that writes it
// and the reader that consumes it can never drift.
public static class ProhibitedItemsEnvelope
{
    // G-28: product-neutral surface key on the shared config primitive.
    public const string SurfaceKey = "moderation-lexicon";

    public const string SurfaceTitle = "Moderation lexicon";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement Serialize(IReadOnlyList<ProhibitedItem> items) =>
        JsonSerializer.SerializeToElement(
            new EnvelopeDto { Items = items.Select(ToDto).ToList() }, Json);

    /// <summary>Items in the published envelope, active-only and in store order.</summary>
    public static IReadOnlyList<ProhibitedItem> ReadActive(JsonElement data)
    {
        var envelope = data.Deserialize<EnvelopeDto>(Json);
        return (envelope?.Items ?? new List<ItemDto>())
            .Where(i => i.Active)
            .Select(FromDto)
            .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ItemDto ToDto(ProhibitedItem i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Category = i.Category,
        Description = i.Description,
        Severity = i.Severity == ProhibitedSeverity.Warn ? "warn" : "block",
        Active = i.Active,
        CreatedBy = i.CreatedBy,
        UpdatedBy = i.UpdatedBy,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt,
    };

    // Unrecognised severity degrades to Block — the same fail-safe default ProhibitedItem carries.
    private static ProhibitedItem FromDto(ItemDto d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Category = d.Category,
        Description = d.Description,
        Severity = string.Equals(d.Severity, "warn", StringComparison.OrdinalIgnoreCase)
            ? ProhibitedSeverity.Warn
            : ProhibitedSeverity.Block,
        Active = d.Active,
        CreatedBy = d.CreatedBy,
        UpdatedBy = d.UpdatedBy,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };

    private sealed class EnvelopeDto
    {
        public List<ItemDto> Items { get; set; } = new();
    }

    private sealed class ItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Severity { get; set; } = "block";
        public bool Active { get; set; }
        public string? CreatedBy { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
