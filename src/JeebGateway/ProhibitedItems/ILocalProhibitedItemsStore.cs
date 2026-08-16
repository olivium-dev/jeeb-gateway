namespace JeebGateway.ProhibitedItems;

/// <summary>
/// gwdbx W3 — the gateway-LOCAL catalog + ack root, resolvable PAST
/// <see cref="StateServiceProhibitedItemsStore"/>.
///
/// <para>Kept so any local-vs-upstream tool resolves the local side EXPLICITLY: once the read rung
/// is live the serving <see cref="IProhibitedItemsStore"/> IS upstream, so resolving it would read
/// upstream, re-publish it and report itself clean. Its last consumers, the freeze-import and the
/// parity check, were retired at ADR-0010; the marker outlives them as the local root.</para>
/// </summary>
public interface ILocalProhibitedItemsStore : IProhibitedItemsStore
{
}
