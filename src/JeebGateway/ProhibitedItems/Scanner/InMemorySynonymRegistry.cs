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
    private readonly object _registrationLock = new();
    private IReadOnlyDictionary<string, string[]> _groups = new Dictionary<string, string[]>();

    public InMemorySynonymRegistry()
    {
        // Conservative seeds: each entry is a phrase that should ALSO flag the
        // canonical item. Keep entries to terms a moderator would expect to see
        // in a free-text delivery description; broad slang lives in the admin
        // alias UI when that ships.
        Register("knife", "knives", "blade", "dagger", "switchblade", "machete", "cleaver");
        Register("gun", "guns", "firearm", "firearms", "pistol", "rifle", "handgun", "handguns", "revolver", "shotgun");
        Register("weapon", "weapons", "sword", "swords", "machete", "machetes");
        // A cylinder is a shape, not evidence of compressed gas. Keep this
        // category phrase-scoped; never reverse-index the bare shape noun.
        Register("compressed gas cylinders", "propane", "butane", "gas cylinder", "gas cylinders", "propane tank", "oxygen cylinder", "gas canister");
        Register("ammunition", "ammo", "bullets", "cartridges");
        Register("explosive", "explosives", "dynamite", "tnt", "c4", "grenade", "detonator");
        Register("drug", "drugs", "narcotic", "narcotics", "cocaine", "heroin", "meth", "methamphetamine", "marijuana", "cannabis", "hashish");
        Register("alcohol", "liquor", "whiskey", "vodka", "beer", "wine");
        Register("fireworks", "firecracker", "firecrackers");
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
        lock (_registrationLock)
        {
            _map[itemName.Trim()] = synonyms?.ToArray() ?? Array.Empty<string>();
            var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var entry in _map)
            {
                var terms = entry.Value.Prepend(entry.Key).Select(TextNormalizer.Normalize)
                    .Where(term => term.Length > 0).Distinct().ToArray();
                foreach (var token in terms.Where(term => TextNormalizer.Tokenize(term).Count == 1))
                {
                    if (!groups.TryGetValue(token, out var group))
                        groups[token] = group = new HashSet<string>(StringComparer.Ordinal);
                    group.UnionWith(terms);
                }
            }
            Volatile.Write(ref _groups, groups.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()));
        }
    }

    public IReadOnlyList<string> ExpandToken(string token) =>
        Volatile.Read(ref _groups).TryGetValue(TextNormalizer.Normalize(token), out var values)
            ? values : Array.Empty<string>();
}
