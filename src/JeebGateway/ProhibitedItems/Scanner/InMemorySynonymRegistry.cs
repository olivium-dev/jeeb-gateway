using System.Collections.Concurrent;

namespace JeebGateway.ProhibitedItems.Scanner;

/// <summary>
/// Seeded with the synonym sets the legal + ops team called out during MVP
/// scoping (T-backend-048). The map is case-insensitive on the item name so
/// catalog entries like "Knife" and "knife" both resolve. Tests and ops can
/// extend it at runtime via <see cref="Register"/>.
/// </summary>
public class InMemorySynonymRegistry : IProhibitedItemSynonymRegistry
{
    private readonly ConcurrentDictionary<string, string[]> _map =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemorySynonymRegistry()
    {
        // Conservative seeds: each entry is a phrase that should ALSO flag the
        // canonical item. Keep entries to terms a moderator would expect to see
        // in a free-text delivery description; broad slang lives in the admin
        // alias UI when that ships.
        Register("knife", "blade", "dagger", "switchblade", "machete", "cleaver");
        Register("gun", "firearm", "pistol", "rifle", "handgun", "revolver", "shotgun");
        Register("ammunition", "ammo", "bullets", "cartridges", "rounds");
        Register("explosive", "explosives", "dynamite", "tnt", "c4", "grenade", "detonator");
        Register("drug", "drugs", "narcotic", "narcotics", "cocaine", "heroin", "meth", "methamphetamine", "marijuana", "cannabis", "hashish");
        Register("alcohol", "liquor", "whiskey", "vodka", "beer", "wine");
        Register("fireworks", "firecracker", "firecrackers", "rocket", "rockets");
        Register("flammable", "gasoline", "petrol", "kerosene", "lighter fluid");
        Register("hazardous material", "hazmat", "toxic", "corrosive", "radioactive");
        Register("counterfeit", "fake currency", "forged", "knockoff");
        Register("medication", "prescription drug", "prescription drugs", "opioid", "opioids");
    }

    public IReadOnlyList<string> GetSynonyms(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return Array.Empty<string>();
        return _map.TryGetValue(itemName.Trim(), out var values)
            ? values
            : Array.Empty<string>();
    }

    public void Register(string itemName, params string[] synonyms)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return;
        _map[itemName.Trim()] = synonyms ?? Array.Empty<string>();
    }
}
