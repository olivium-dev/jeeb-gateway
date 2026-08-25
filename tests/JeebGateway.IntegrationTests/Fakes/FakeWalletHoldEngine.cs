using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Collections;
using JeebGateway.Financials;
using JeebGateway.Financials.Refunds;
using JeebGateway.service.ServiceWallet;
using SwServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>W3/T2 — ONE in-memory wallet ledger behind BOTH halves the design reads: the two-phase
/// client AND the pending-NETTED holder read (without which the L2 hold tests prove nothing).</summary>
/// <remarks>Per RESEARCH-holds §1–§2: initiate validates <c>balance − Σ pending</c> and moves no
/// money, execute is the only mutation, abort is idempotent and never clobbers an executed header.</remarks>
public sealed class FakeWalletHoldEngine : IWalletCommissionDebitClient
{
    // wallet-service TransactionStatus names (wire ints -1/0/-2) as the frozen
    // I2 HoldHeader.Status strings.
    public const string StatusPending = "Pending";
    public const string StatusExecuted = "Executed";
    public const string StatusAborted = "Aborted";

    /// <summary>Frozen fee tags: the capture's <c>CommissionCollectionOptions.Tag</c> default and
    /// the W5 CONTRACT §4 refund tag.</summary>
    public const string CaptureTag = "platform-fee";

    public const string RefundTag = "platform-fee-refund";

    /// <summary>Fixed platform counterparty, mirroring <c>ResolveSystemWalletAsync</c>'s
    /// <c>__SYSTEM__</c> holder wallet.</summary>
    public static readonly Guid SystemWalletId = new("5f57e1ec-0000-4000-8000-00000000fee5");

    private readonly object _gate = new();
    private readonly Dictionary<Guid, decimal> _balances = new();
    private readonly Dictionary<Guid, Guid> _feeWallets = new();
    private readonly Dictionary<Guid, Guid> _walletOwners = new();
    private readonly Dictionary<string, Guid> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly List<HoldEntry> _entries = new();

    /// <summary>Gross (un-netted) starting balance for every holder that has not been seeded
    /// with <see cref="SetBalance"/>. Generous by default so unrelated guards never trip.</summary>
    public decimal Balance { get; set; } = 1_000_000m;

    /// <summary>Flag-OFF pin log: MUST stay empty while <c>CommissionCollection:Enabled=false</c>
    /// (DECISION invariant I3). <c>Should().Be(n)</c> asserts the call COUNT, as it always did.</summary>
    public CallLog<Guid> ExecuteCalls { get; } = new();

    public CallLog<Guid> AbortCalls { get; } = new();

    /// <summary>Every initiate attempt, refused ones included, with the frozen body fields the
    /// W5 refund-naming assertions read.</summary>
    public CallLog<(Guid Source, Guid Destination, decimal Amount, string Tag, string IdempotencyKey, string ExternalReference)> InitiateCalls { get; } = new();

    /// <summary>initiate+execute+abort — the single "did the gateway touch money at all" number
    /// the flag-off money-neutrality pins assert is 0.</summary>
    public int MutationCallCount => InitiateCalls.Count + ExecuteCalls.Count + AbortCalls.Count;

    /// <summary>Drives the "no fee wallet resolvable" branch, which the hold placer maps to
    /// E6 503 wallet-service-unavailable.</summary>
    public bool FeeWalletUnresolvable { get; set; }

    public bool SystemWalletUnresolvable { get; set; }

    /// <summary>One-shot fault for the NEXT initiate, so 402-vs-503 mapping is injectable without
    /// a network or a clock; takes an <see cref="InitiateFault"/> or the bare <c>true</c> sugar.</summary>
    public InitiateFaultScript? FailNextInitiate { get; set; }

    /// <summary>Per-key failure scripting: invoked with the idempotency key BEFORE the call is
    /// processed, so throwing from it injects a fault on exactly the transaction under test.</summary>
    public Action<string>? OnInitiate { get; set; }

    /// <summary>Frozen external reference (DECISION Naming): one ref per offer, shared by its
    /// base + delta holds.</summary>
    public static string OfferReference(string offerId) => $"jeeb:offer:{offerId}";

    /// <summary>The capture/refund reference pair: one ref per delivery, straight off the
    /// production helper so a test can never drift from the frozen shape.</summary>
    public static string DeliveryReference(string requestId)
        => WalletCommissionCollector.ExternalReferenceFor(requestId);

    /// <summary>Stable per-holder fee wallet id; the same Guid for the whole engine lifetime so
    /// a hold placed under it nets out of that holder's later reads.</summary>
    public Guid FeeWalletFor(Guid holderId)
    {
        lock (_gate)
        {
            return FeeWalletForLocked(holderId);
        }
    }

    public void SetBalance(Guid holderId, decimal amount)
    {
        lock (_gate)
        {
            FeeWalletForLocked(holderId);
            _balances[holderId] = amount;
        }
    }

    /// <summary>What the wallet actually holds, before pending holds are subtracted.</summary>
    public decimal GrossBalance(Guid holderId)
    {
        lock (_gate)
        {
            return GrossBalanceLocked(holderId);
        }
    }

    /// <summary>S-10 netted read: gross minus this holder's pending OUTGOING legs — the number
    /// the fee guard sees, and the reason a placed hold shrinks the spendable balance.</summary>
    public decimal NettedBalance(Guid holderId)
    {
        lock (_gate)
        {
            var wallet = FeeWalletForLocked(holderId);
            return GrossBalanceLocked(holderId) - PendingOutgoingLocked(wallet);
        }
    }

    /// <summary>Every header under <paramref name="externalReference"/>, newest-first (any status).</summary>
    public IReadOnlyList<HoldRecord> Headers(string externalReference)
    {
        lock (_gate)
        {
            return SnapshotLocked(e => Matches(e, externalReference));
        }
    }

    /// <summary>Pending headers only — the leak assertion for every Op-3 release trigger
    /// (empty == nothing frozen).</summary>
    public IReadOnlyList<HoldRecord> PendingHeaders(string? externalReference = null)
    {
        lock (_gate)
        {
            return SnapshotLocked(e => e.Status == StatusPending
                && (externalReference is null || Matches(e, externalReference)));
        }
    }

    /// <summary>Σ pending amounts under one external reference — the design's <c>heldTotal</c>.</summary>
    public decimal HeldTotal(string externalReference)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Status == StatusPending && Matches(e, externalReference))
                .Sum(e => e.Amount);
        }
    }

    /// <summary>A wallet client over THIS ledger: one netted USD(2) spendable wallet per holder.</summary>
    public HoldAwareFakeWalletClient NewWalletClient() => new(this);

    /// <summary>TESTING §5.1 barrier-gated variant: all <paramref name="participants"/> concurrent
    /// reads observe the identical un-reserved balance, forcing the TOCTOU window every run.</summary>
    public GatedWalletClient NewGatedWalletClient(int participants) => new(this, participants);

    // ── IWalletCommissionDebitClient ──

    public Task<Guid?> ResolveFeeWalletAsync(Guid holderId, CancellationToken ct)
        => Task.FromResult<Guid?>(FeeWalletUnresolvable ? null : FeeWalletFor(holderId));

    public Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct)
        => Task.FromResult<Guid?>(SystemWalletUnresolvable ? null : SystemWalletId);

    /// <summary>Existing 8-arg capture shape; the debit leg is an additional-fees leg (I2).</summary>
    public Task<Guid> InitiateAsync(
        Guid sourceWalletId, Guid destinationWalletId, decimal amount,
        string tag, string notes, string idempotencyKey, string externalReference, CancellationToken ct)
        => InitiateAsync(sourceWalletId, destinationWalletId, amount, tag, notes,
            idempotencyKey, externalReference, isAdditionalFees: true, ct);

    public Task<Guid> InitiateAsync(
        Guid sourceWalletId, Guid destinationWalletId, decimal amount,
        string tag, string notes, string idempotencyKey, string externalReference,
        bool isAdditionalFees, CancellationToken ct)
    {
        lock (_gate)
        {
            InitiateCalls.Add((sourceWalletId, destinationWalletId, amount, tag, idempotencyKey, externalReference));

            try
            {
                OnInitiate?.Invoke(idempotencyKey);
            }
            catch (Exception scripted)
            {
                return Task.FromException<Guid>(scripted);
            }

            if (FailNextInitiate is { Armed: true } script)
            {
                FailNextInitiate = null;
                return Task.FromException<Guid>(FaultFor(script.Fault));
            }

            // Idempotent replay is fingerprint-scoped upstream: same key + same legs replays the
            // original txId, same key + different body is a real 409 idempotency-conflict.
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var replayed))
            {
                var original = _entries.First(e => e.TxId == replayed);
                var sameBody = original.SourceWalletId == sourceWalletId
                    && original.DestinationWalletId == destinationWalletId
                    && original.Amount == amount;
                return sameBody
                    ? Task.FromResult(replayed)
                    : Task.FromException<Guid>(FaultFor(InitiateFault.IdempotencyConflict));
            }

            var holderId = OwnerOfLocked(sourceWalletId);
            var available = GrossBalanceLocked(holderId) - PendingOutgoingLocked(sourceWalletId);
            if (amount > available)
            {
                return Task.FromException<Guid>(FaultFor(InitiateFault.InsufficientBalance));
            }

            var entry = new HoldEntry
            {
                TxId = Guid.NewGuid(),
                HolderId = holderId,
                SourceWalletId = sourceWalletId,
                DestinationWalletId = destinationWalletId,
                Amount = amount,
                Tag = tag,
                Notes = notes,
                IdempotencyKey = idempotencyKey,
                ExternalReference = externalReference,
                IsAdditionalFees = isAdditionalFees,
                Status = StatusPending,
            };
            _entries.Add(entry);
            _byIdempotencyKey[idempotencyKey] = entry.TxId;
            return Task.FromResult(entry.TxId);
        }
    }

    /// <summary>Existing first-txId read (newest-first), unchanged by the hold work.</summary>
    public Task<Guid?> FindByExternalReferenceAsync(string externalReference, CancellationToken ct)
    {
        lock (_gate)
        {
            var newest = SnapshotLocked(e => Matches(e, externalReference)).FirstOrDefault();
            return Task.FromResult<Guid?>(newest.TxId == Guid.Empty ? null : newest.TxId);
        }
    }

    /// <summary>I2 — the FULL header set for a reference (status + amount per header); empty
    /// when the reference is unknown, mirroring the upstream 404.</summary>
    public Task<IReadOnlyList<HoldHeader>> ListByExternalReferenceAsync(
        string externalReference, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<HoldHeader> headers = SnapshotLocked(e => Matches(e, externalReference))
                .Select(h => new HoldHeader(h.TxId, h.Status, h.Amount))
                .ToArray();
            return Task.FromResult(headers);
        }
    }

    /// <summary>W5 §3 — the richer ledger read the refunder decides on: Tag, Status and both legs
    /// per header, newest-first; empty for an unknown reference (upstream 404).</summary>
    public Task<IReadOnlyList<FeeLedgerEntry>> ListFeeLedgerByExternalReferenceAsync(
        string externalReference, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<FeeLedgerEntry> rows = _entries
                .Where(e => Matches(e, externalReference))
                .Reverse()
                .Select(e => new FeeLedgerEntry(
                    e.TxId, e.Tag, e.Status, e.Amount, e.SourceWalletId, e.DestinationWalletId))
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    /// <summary>Arranges the POST-CAPTURE world: one EXECUTED platform-fee debit feeWallet→system
    /// under <c>delivery:{requestId}</c>, without counting as a mutation call the pins read.</summary>
    public Guid SeedExecutedCapture(string requestId, decimal amount, Guid feeWalletId)
    {
        lock (_gate)
        {
            var entry = new HoldEntry
            {
                TxId = Guid.NewGuid(),
                HolderId = OwnerOfLocked(feeWalletId),
                SourceWalletId = feeWalletId,
                DestinationWalletId = SystemWalletId,
                Amount = amount,
                Tag = CaptureTag,
                Notes = "seeded capture",
                IdempotencyKey = WalletCommissionCollector.IdempotencyKeyFor(requestId),
                ExternalReference = WalletCommissionCollector.ExternalReferenceFor(requestId),
                IsAdditionalFees = true,
                Status = StatusExecuted,
            };
            _entries.Add(entry);
            _byIdempotencyKey[entry.IdempotencyKey] = entry.TxId;
            return entry.TxId;
        }
    }

    /// <summary>Arranges the ALREADY-REFUNDED world: one EXECUTED platform-fee-refund credit
    /// system→feeWallet under the same delivery ref, again without counting as a mutation call.</summary>
    public Guid SeedExecutedRefund(string requestId, decimal amount, Guid feeWalletId)
    {
        lock (_gate)
        {
            var entry = new HoldEntry
            {
                TxId = Guid.NewGuid(),
                HolderId = OwnerOfLocked(feeWalletId),
                SourceWalletId = SystemWalletId,
                DestinationWalletId = feeWalletId,
                Amount = amount,
                Tag = RefundTag,
                Notes = "seeded refund",
                IdempotencyKey = FeeRefunder.IdempotencyKeyFor(requestId),
                ExternalReference = WalletCommissionCollector.ExternalReferenceFor(requestId),
                IsAdditionalFees = false,
                Status = StatusExecuted,
            };
            _entries.Add(entry);
            _byIdempotencyKey[entry.IdempotencyKey] = entry.TxId;
            return entry.TxId;
        }
    }

    public Task ExecuteAsync(Guid transactionId, CancellationToken ct)
    {
        lock (_gate)
        {
            ExecuteCalls.Add(transactionId);
            var entry = _entries.FirstOrDefault(e => e.TxId == transactionId);
            if (entry is null)
            {
                return Task.FromException(new WalletCommissionDebitException(
                    "unknown transaction", HttpStatusCode.NotFound));
            }
            if (entry.Status == StatusExecuted) return Task.CompletedTask; // idempotent by txId
            if (entry.Status == StatusAborted)
            {
                return Task.FromException(new WalletCommissionDebitException(
                    "Transaction aborted", HttpStatusCode.InternalServerError));
            }

            entry.Status = StatusExecuted;
            _balances[entry.HolderId] = GrossBalanceLocked(entry.HolderId) - entry.Amount;
            return Task.CompletedTask;
        }
    }

    public Task AbortAsync(Guid transactionId, CancellationToken ct)
    {
        lock (_gate)
        {
            AbortCalls.Add(transactionId);
            var entry = _entries.FirstOrDefault(e => e.TxId == transactionId);
            // Unknown / already aborted: releasing twice is a success upstream, never an error.
            if (entry is null || entry.Status == StatusAborted) return Task.CompletedTask;
            if (entry.Status == StatusExecuted)
            {
                return Task.FromException(new WalletCommissionDebitException(
                    "Transaction already executed", HttpStatusCode.InternalServerError));
            }

            entry.Status = StatusAborted;
            return Task.CompletedTask;
        }
    }

    // ── internals (caller holds _gate) ──

    private static bool Matches(HoldEntry entry, string externalReference)
        => string.Equals(entry.ExternalReference, externalReference, StringComparison.Ordinal);

    private IReadOnlyList<HoldRecord> SnapshotLocked(Func<HoldEntry, bool> predicate)
        => _entries
            .Where(predicate)
            .Reverse()
            .Select(e => new HoldRecord(
                e.TxId, e.HolderId, e.ExternalReference, e.IdempotencyKey, e.Amount, e.Status,
                e.Tag, e.IsAdditionalFees))
            .ToArray();

    private Guid FeeWalletForLocked(Guid holderId)
    {
        if (_feeWallets.TryGetValue(holderId, out var walletId)) return walletId;
        walletId = Guid.NewGuid();
        _feeWallets[holderId] = walletId;
        _walletOwners[walletId] = holderId;
        return walletId;
    }

    private Guid OwnerOfLocked(Guid walletId)
        => _walletOwners.TryGetValue(walletId, out var holderId) ? holderId : Guid.Empty;

    private decimal GrossBalanceLocked(Guid holderId)
        => _balances.TryGetValue(holderId, out var amount) ? amount : Balance;

    private decimal PendingOutgoingLocked(Guid walletId)
        => _entries
            .Where(e => e.Status == StatusPending && e.SourceWalletId == walletId)
            .Sum(e => e.Amount);

    private static WalletCommissionDebitException FaultFor(InitiateFault fault) => fault switch
    {
        InitiateFault.InsufficientBalance => new WalletCommissionDebitException(
            "wallet-service refused the hold (insufficient balance).", HttpStatusCode.Conflict,
            WalletCommissionDebitException.InsufficientBalanceType),
        InitiateFault.IdempotencyConflict => new WalletCommissionDebitException(
            "wallet-service reported an idempotency conflict.", HttpStatusCode.Conflict,
            WalletCommissionDebitException.IdempotencyConflictType),
        InitiateFault.ServerError => new WalletCommissionDebitException(
            "wallet-service failed to initiate the hold.", HttpStatusCode.InternalServerError),
        _ => new WalletCommissionDebitException(
            "wallet-service transport fault while trying to initiate the hold.", null, null,
            new HttpRequestException("simulated wallet-service transport fault")),
    };

    /// <summary>Injectable initiate failures: the 402 leg (insufficiency) and the three 503 legs
    /// (conflict / 5xx / transport) the design maps to E6.</summary>
    public enum InitiateFault
    {
        InsufficientBalance,
        IdempotencyConflict,
        ServerError,
        Transport,
    }

    /// <summary>What <see cref="FailNextInitiate"/> holds: an explicit fault, or the bare
    /// <c>true</c>/<c>false</c> sugar which arms/disarms the generic server-error leg.</summary>
    public readonly record struct InitiateFaultScript(bool Armed, InitiateFault Fault)
    {
        public static implicit operator InitiateFaultScript(InitiateFault fault) => new(true, fault);

        public static implicit operator InitiateFaultScript(bool armed)
            => new(armed, InitiateFault.ServerError);
    }

    /// <summary>Assertion projection of one wallet-service transaction header; Tag and
    /// IsAdditionalFees pin the frozen hold body constants ("hold" / false).</summary>
    public readonly record struct HoldRecord(
        Guid TxId,
        Guid HolderId,
        string ExternalReference,
        string IdempotencyKey,
        decimal Amount,
        string Status,
        string Tag,
        bool IsAdditionalFees);

    private sealed class HoldEntry
    {
        public Guid TxId { get; init; }
        public Guid HolderId { get; init; }
        public Guid SourceWalletId { get; init; }
        public Guid DestinationWalletId { get; init; }
        public decimal Amount { get; init; }
        public string Tag { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public string IdempotencyKey { get; init; } = string.Empty;
        public string ExternalReference { get; init; } = string.Empty;
        public bool IsAdditionalFees { get; init; }
        public string Status { get; set; } = StatusPending;
    }

    /// <summary>The holder read over the SAME ledger: one active spendable USD(2) wallet whose
    /// Amount is the NETTED balance, exactly as wallet-service reports it (S-10).</summary>
    public class HoldAwareFakeWalletClient : SwServiceWalletClient
    {
        private readonly FakeWalletHoldEngine _engine;

        internal HoldAwareFakeWalletClient(FakeWalletHoldEngine engine)
            : base("http://localhost", new HttpClient())
        {
            _engine = engine;
        }

        /// <summary>Wallet-service outage sentinel, same shape as <see cref="FakeWalletClient"/>.</summary>
        public bool Unreachable { get; set; }

        /// <summary>Repo fee-currency default: USD(2) in the wallet-service catalog.</summary>
        public int CurrencyId { get; set; } = 2;

        private int _reads;

        /// <summary>How many balance reads this client served — proves the guard actually read.</summary>
        public int Reads => Volatile.Read(ref _reads);

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
        {
            if (Unreachable) throw new HttpRequestException("simulated wallet-service outage");

            Interlocked.Increment(ref _reads);
            OnBalanceRead();
            return Task.FromResult(new GetHolderWallets
            {
                WalletHolder = new WalletHolder { HolderId = holderId, HolderName = "fake", IsActive = true },
                Wallets = new List<Wallet>
                {
                    new()
                    {
                        WalletId = _engine.FeeWalletFor(holderId),
                        HolderId = holderId,
                        CurrencyID = CurrencyId,
                        Amount = (double)_engine.NettedBalance(holderId),
                        IsActive = true,
                        Type = "main",
                    },
                },
            });
        }

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId)
            => WalletsAsync(holderId, CancellationToken.None);

        /// <summary>Seam the gated variant uses to hold every concurrent reader at the same
        /// pre-reservation balance.</summary>
        protected virtual void OnBalanceRead()
        {
        }
    }

    /// <summary>TESTING §5.1 gate: N concurrent balance reads are released together, so the
    /// check-then-act window is forced on every iteration instead of hoped for.</summary>
    public sealed class GatedWalletClient : HoldAwareFakeWalletClient
    {
        private readonly Barrier _barrier;
        private volatile bool _disarmed;

        internal GatedWalletClient(FakeWalletHoldEngine engine, int participants)
            : base(engine)
        {
            _barrier = new Barrier(participants);
        }

        /// <summary>Self-disarm budget. Serialized callers can never all arrive, so the gate
        /// retires itself once instead of deadlocking the suite (never a timing assertion).</summary>
        public int GateTimeoutMs { get; set; } = 250;

        protected override void OnBalanceRead()
        {
            if (_disarmed) return;
            if (!_barrier.SignalAndWait(GateTimeoutMs)) _disarmed = true;
        }
    }
}

/// <summary>A wallet-call log that answers BOTH shapes the suites need: the W3/W4 count pin
/// (<c>Should().Be(n)</c>) and the W5 argument list, so no existing call site changed.</summary>
public sealed class CallLog<T> : IReadOnlyList<T>
{
    private readonly object _gate = new();
    private readonly List<T> _items = new();

    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    public T this[int index]
    {
        get { lock (_gate) { return _items[index]; } }
    }

    /// <summary>Instance <c>Should()</c>: it binds ahead of the FluentAssertions extension, which
    /// is what lets one member carry the count assertion and the collection assertions.</summary>
    public CallLogAssertions<T> Should() => new(this);

    public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(T item)
    {
        lock (_gate) { _items.Add(item); }
    }

    // Enumeration snapshots under the gate: the concurrency suite writes from many threads.
    private List<T> Snapshot()
    {
        lock (_gate) { return new List<T>(_items); }
    }
}

/// <summary>The full FluentAssertions collection surface plus the legacy counter form
/// <c>Be(n)</c>, so widening the int counters into logs is source-compatible.</summary>
public sealed class CallLogAssertions<T>
    : GenericCollectionAssertions<IEnumerable<T>, T, CallLogAssertions<T>>
{
    internal CallLogAssertions(IEnumerable<T> subject)
        : base(subject)
    {
    }

    /// <summary>Counter form: how many calls were logged.</summary>
    public AndConstraint<CallLogAssertions<T>> Be(
        int expected, string because = "", params object[] becauseArgs)
    {
        Subject.Count().Should().Be(expected, because, becauseArgs);
        return new AndConstraint<CallLogAssertions<T>>(this);
    }
}
