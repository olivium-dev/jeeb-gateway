namespace JeebGateway.ProhibitedItems;

public class ProhibitedItem
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public string? Description { get; set; }
    public bool Active { get; set; } = true;
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
