namespace JeebGateway.Chat;

/// <summary>
/// Canonical conversation id derived from a participant pair. Sorting the
/// two user ids lexicographically and joining with '|' guarantees that
/// either side can compute the same key without a server round-trip, and
/// that the store never accidentally splits a conversation into two rows
/// because A→B and B→A computed different keys.
/// </summary>
public static class ConversationKey
{
    public static string For(string userA, string userB)
    {
        if (string.IsNullOrWhiteSpace(userA)) throw new ArgumentException("userA required", nameof(userA));
        if (string.IsNullOrWhiteSpace(userB)) throw new ArgumentException("userB required", nameof(userB));
        if (string.Equals(userA, userB, StringComparison.Ordinal))
            throw new ArgumentException("self-conversation is not supported", nameof(userB));

        return string.CompareOrdinal(userA, userB) < 0
            ? $"{userA}|{userB}"
            : $"{userB}|{userA}";
    }
}
