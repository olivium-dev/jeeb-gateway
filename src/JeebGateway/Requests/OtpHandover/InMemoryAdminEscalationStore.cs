using System.Collections.Concurrent;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// MVP in-memory implementation of <see cref="IAdminEscalationStore"/>.
/// Production swap moves the rows to Postgres alongside
/// <c>admin_actions</c> in db/migrations/0005.
/// </summary>
public class InMemoryAdminEscalationStore : IAdminEscalationStore
{
    private readonly ConcurrentDictionary<string, AdminEscalation> _byId = new();

    public Task<AdminEscalation> CreateAsync(AdminEscalation entry, CancellationToken ct)
    {
        _byId[entry.Id] = entry;
        return Task.FromResult(entry);
    }

    public Task<AdminEscalation?> GetForDeliveryAsync(string deliveryId, string reason, CancellationToken ct)
    {
        var match = _byId.Values.FirstOrDefault(e =>
            string.Equals(e.DeliveryId, deliveryId, StringComparison.Ordinal)
            && string.Equals(e.Reason, reason, StringComparison.Ordinal));
        return Task.FromResult<AdminEscalation?>(match);
    }

    public Task<IReadOnlyList<AdminEscalation>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<AdminEscalation> snapshot = _byId.Values
            .OrderBy(e => e.CreatedAt)
            .ToArray();
        return Task.FromResult(snapshot);
    }
}
