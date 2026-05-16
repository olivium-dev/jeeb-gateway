namespace JeebGateway.ProhibitedItems.Scanner;

public interface IProhibitedItemScanner
{
    Task<ProhibitedItemScanResult> ScanAsync(string? description, CancellationToken ct);
}
