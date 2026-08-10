namespace JeebGateway.ProhibitedItems.Scanner;

public interface IProhibitedItemScanner
{
    Task<ProhibitedItemScanResult> ScanAsync(string? description, CancellationToken ct);

    Task<ProhibitedItemScanResult> ScanAsync(
        string? description,
        IReadOnlyList<ProhibitedItem> items,
        CancellationToken ct);
}
