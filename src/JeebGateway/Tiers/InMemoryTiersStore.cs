using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace JeebGateway.Tiers;

/// <summary>
/// MVP-grade in-memory tier catalog. Each read returns a deep-cloned snapshot;
/// each write takes a short critical section so the uniqueness check and the
/// insert/update form a single atomic block.
///
/// Seeded with the three default tiers (Urgent, Same-Day, Scheduled) on
/// construction. Admins may add/edit/remove rows; the seeded
/// rows are not protected — removing them is intentionally allowed so a
/// product change does not require a code change.
/// </summary>
public class InMemoryTiersStore : ITiersStore
{
    private static readonly Regex SlugFormat =
        new("^[a-z][a-z0-9-]{1,47}$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, DeliveryTier> _tiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();

    public InMemoryTiersStore()
    {
        Seed();
    }

    public Task<IReadOnlyList<DeliveryTier>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<DeliveryTier> list = _tiers.Values
            .OrderBy(t => t.SlaHours)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToList();

        return Task.FromResult(list);
    }

    public Task<DeliveryTier?> GetAsync(string id, CancellationToken ct)
    {
        if (_tiers.TryGetValue(id, out var t))
        {
            return Task.FromResult<DeliveryTier?>(Clone(t));
        }

        return Task.FromResult<DeliveryTier?>(null);
    }

    public Task<DeliveryTier> CreateAsync(DeliveryTierCreate input, string adminUserId, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(input.Id) ? Slugify(input.Name) : input.Id.Trim();
        var name = input.Name.Trim();
        var priceHint = input.PriceHint.Trim();
        var now = DateTimeOffset.UtcNow;

        var tier = new DeliveryTier
        {
            Id = id,
            Name = name,
            SlaHours = input.SlaHours,
            RadiusKm = input.RadiusKm,
            RequestTtlSeconds = input.RequestTtlSeconds,
            CommissionRate = input.CommissionRate,
            PriceHint = priceHint,
            CreatedBy = adminUserId,
            UpdatedBy = adminUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_writeLock)
        {
            if (_tiers.ContainsKey(id)) throw new DuplicateTierIdException(id);
            if (HasNameConflict(name, excludingId: null)) throw new DuplicateTierNameException(name);
            _tiers[id] = tier;
        }

        return Task.FromResult(Clone(tier));
    }

    public Task<DeliveryTier?> ReplaceAsync(string id, DeliveryTierReplace input, string adminUserId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_tiers.TryGetValue(id, out var existing))
            {
                return Task.FromResult<DeliveryTier?>(null);
            }

            var name = input.Name.Trim();
            if (HasNameConflict(name, excludingId: existing.Id))
            {
                throw new DuplicateTierNameException(name);
            }

            existing.Name = name;
            existing.SlaHours = input.SlaHours;
            existing.RadiusKm = input.RadiusKm;
            existing.RequestTtlSeconds = input.RequestTtlSeconds;
            existing.CommissionRate = input.CommissionRate;
            existing.PriceHint = input.PriceHint.Trim();
            existing.UpdatedBy = adminUserId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            return Task.FromResult<DeliveryTier?>(Clone(existing));
        }
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        lock (_writeLock)
        {
            return Task.FromResult(_tiers.TryRemove(id, out _));
        }
    }

    private bool HasNameConflict(string name, string? excludingId)
    {
        foreach (var t in _tiers.Values)
        {
            if (excludingId is not null && string.Equals(t.Id, excludingId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Slugify(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var hyphenated = Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(hyphenated) ? Guid.NewGuid().ToString("n")[..8] : hyphenated;
    }

    private static DeliveryTier Clone(DeliveryTier t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        SlaHours = t.SlaHours,
        RadiusKm = t.RadiusKm,
        RequestTtlSeconds = t.RequestTtlSeconds,
        CommissionRate = t.CommissionRate,
        PriceHint = t.PriceHint,
        CreatedBy = t.CreatedBy,
        UpdatedBy = t.UpdatedBy,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt
    };

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var t in DefaultTiers(now))
        {
            _tiers[t.Id] = t;
        }
    }

    internal static bool IsValidSlug(string slug) => SlugFormat.IsMatch(slug);

    private static IEnumerable<DeliveryTier> DefaultTiers(DateTimeOffset now)
    {
        yield return new DeliveryTier
        {
            Id = "urgent",
            Name = "Urgent",
            SlaHours = 1,
            RadiusKm = 3.0,
            RequestTtlSeconds = 30 * 60,
            CommissionRate = 0.25,
            PriceHint = "Premium — fastest dispatch, top-of-list matching",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "system",
            UpdatedBy = "system"
        };
        yield return new DeliveryTier
        {
            Id = "same-day",
            Name = "Same-Day",
            SlaHours = 2,
            RadiusKm = 10.0,
            RequestTtlSeconds = 2 * 60 * 60,
            CommissionRate = 0.20,
            PriceHint = "Standard same-day rate",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "system",
            UpdatedBy = "system"
        };
        yield return new DeliveryTier
        {
            Id = "scheduled",
            Name = "Scheduled",
            SlaHours = 24,
            RadiusKm = 25.0,
            RequestTtlSeconds = 24 * 60 * 60,
            CommissionRate = 0.15,
            PriceHint = "Choose a delivery window up to 24h ahead",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = "system",
            UpdatedBy = "system"
        };
    }
}
