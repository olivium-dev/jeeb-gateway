namespace JeebGateway.ProhibitedItems;

/// <summary>
/// gwdbx W3 — the gateway-LOCAL catalog + ack root, resolvable PAST
/// <see cref="StateServiceProhibitedItemsStore"/>.
///
/// <para>The freeze-import and the parity check must always compare local-vs-upstream. Once the
/// read rung is live the serving <see cref="IProhibitedItemsStore"/> IS upstream, so a tool that
/// resolved the serving interface would read upstream, re-publish it, and report itself clean —
/// the two-writable-catalogs shape ADR-0008 forbids. They resolve this marker instead.</para>
/// </summary>
public interface ILocalProhibitedItemsStore : IProhibitedItemsStore
{
}
