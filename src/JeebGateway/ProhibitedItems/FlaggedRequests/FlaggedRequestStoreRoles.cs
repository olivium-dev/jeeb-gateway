namespace JeebGateway.ProhibitedItems.FlaggedRequests;

/// <summary>
/// gwdbx W3 — the gateway-LOCAL moderation queue root, resolvable past whatever the mode binds to
/// <see cref="IFlaggedRequestStore"/>. Same reason as
/// <see cref="JeebGateway.ProhibitedItems.ILocalProhibitedItemsStore"/>: import and parity must
/// read the local side explicitly, never the serving side.
/// </summary>
public interface ILocalFlaggedRequestStore : IFlaggedRequestStore
{
}

/// <summary>
/// gwdbx W3 — the UPSTREAM moderation queue (jeeb-state-service generic cases, kind
/// <c>moderation_review</c>). One upstream for the whole leg: the import replays onto exactly the
/// surface the read rung serves from, so importer and store can no longer disagree.
/// </summary>
public interface IUpstreamFlaggedRequestStore : IFlaggedRequestStore
{
}
