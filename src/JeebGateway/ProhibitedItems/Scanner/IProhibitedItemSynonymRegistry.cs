namespace JeebGateway.ProhibitedItems.Scanner;

/// <summary>
/// Synonyms expand the matcher's recall without forcing admins to enumerate
/// every street name and slang term in the catalog UI. The registry is
/// keyed by the canonical item name (case-insensitive); lookups return the
/// extra surface forms to try in addition to the catalog name itself.
/// </summary>
public interface IProhibitedItemSynonymRegistry
{
    IReadOnlyList<string> GetSynonyms(string itemName);
}
