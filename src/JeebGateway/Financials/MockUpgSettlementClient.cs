using System.Collections.Concurrent;

namespace JeebGateway.Financials;

/// <summary>
/// MOCK Unified-Payment-Gateway transport (Plan 3, decision D1 —
/// <c>plan3/01-CTO-DECISIONS.md</c>; JEB-56/JEB-57). Substitutes the UPG
/// upstream so the gateway's flag-ON settlement sync path
/// (<see cref="UpgSettlementLedgerClient"/> → <see cref="IUpgSettlementClient"/>)
/// can be validated while the real UPG deploy remains owner-gated
/// (upg PRs #8/#9/#10, HG-1).
///
/// P9 mock-honesty contract:
/// <list type="bullet">
///   <item>Reachable ONLY when BOTH <c>FeatureFlags:UseUpstream:Payments</c>
///         AND <c>FeatureFlags:UseUpstream:PaymentsMock</c> are true (DI gate
///         in Program.cs). Production sets neither — the real-money path can
///         never hit this class without an explicit double env flip.</item>
///   <item>Named <c>Mock*</c> so <c>grep -rn MockUpgSettlementClient</c> finds it.</item>
///   <item>Pure transport — NO settlement math here (GR2); fees are computed
///         upstream in <see cref="SettlementService"/> / <see cref="CommissionCalculator"/>.</item>
///   <item>TECH-DEBT: removal is tracked by the "[Tech Debt][Payments] Real UPG
///         cutover — owner sign-off + live upstream settlement" ticket created
///         by PO-1 per WS-P1 §7 (id recorded on JEB-56/57).</item>
/// </list>
///
/// Behavior mirrors UPG's generic external-settlement primitive: idempotent on
/// <c>externalRef</c> (UPG keys on <c>(source, externalRef)</c>; the gateway
/// posts a single fixed source, <see cref="UpgSettlementLedgerClient.Source"/>),
/// deterministic settlement ids, and every recorded request is retained
/// in-memory so tests can assert the exact mapped
/// source/externalRef/payeeRef/gross/fee/net values.
/// </summary>
public sealed class MockUpgSettlementClient : IUpgSettlementClient
{
    public const string SettlementIdPrefix = "mock-upg-";

    private readonly ConcurrentDictionary<string, UpgSettlementRequest> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Recorded settlement requests keyed by <c>externalRef</c> (= deliveryId).
    /// One entry per externalRef regardless of replay count — the observable
    /// idempotency surface for T2/T5.
    /// </summary>
    public IReadOnlyDictionary<string, UpgSettlementRequest> Entries => _entries;

    public Task<UpgSettlementResponse> RecordSettlementAsync(UpgSettlementRequest request, CancellationToken ct)
    {
        // Idempotent replay: the first request for an externalRef wins; a
        // repeat returns the original entry's deterministic id (no duplicate).
        _entries.GetOrAdd(request.ExternalRef, _ => request);

        return Task.FromResult(new UpgSettlementResponse
        {
            SettlementId = SettlementIdPrefix + request.ExternalRef,
            Status = "recorded",
        });
    }
}
