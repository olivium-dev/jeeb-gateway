using System.Collections.Concurrent;
using JeebGateway.Users.DataExport;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Test double for <see cref="IDataExportChatHistoryProvider"/>. Production deleted
/// the in-memory provider in favour of a client-backed one that returns empty until
/// the generic chat-service grows a list-channels-for-member endpoint. The export
/// PIPELINE behaviour (packager includes chat history when the provider yields it)
/// is still worth asserting, so the data-export test swaps in this seedable double.
/// </summary>
public sealed class SeedableDataExportChatHistoryProvider : IDataExportChatHistoryProvider
{
    private readonly ConcurrentDictionary<string, List<ChatMessageSnapshot>> _byUser =
        new(StringComparer.Ordinal);

    public Task<IReadOnlyList<ChatMessageSnapshot>> GetForUserAsync(string userId, CancellationToken ct)
    {
        if (_byUser.TryGetValue(userId, out var list))
        {
            IReadOnlyList<ChatMessageSnapshot> snapshot = list.OrderBy(m => m.SentAt).ToArray();
            return Task.FromResult(snapshot);
        }
        return Task.FromResult<IReadOnlyList<ChatMessageSnapshot>>(Array.Empty<ChatMessageSnapshot>());
    }

    public void Seed(string userId, params ChatMessageSnapshot[] messages) =>
        _byUser.AddOrUpdate(
            userId,
            _ => messages.ToList(),
            (_, existing) => { existing.AddRange(messages); return existing; });
}
