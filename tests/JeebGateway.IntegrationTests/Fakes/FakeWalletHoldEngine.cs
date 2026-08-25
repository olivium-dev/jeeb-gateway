using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Financials;
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

    /// <summary>Flag-OFF pin counter: MUST stay 0 while <c>CommissionCollection:Enabled=false</c>
    /// (DECISION invariant I3).</summary>
    public int ExecuteCalls { get; private set; }

    public int AbortCalls { get; private set; }

    public int InitiateCalls { get; private set; }

    /// <summary>Drives the "no fee wallet resolvable" branch, which the hold placer maps to
    /// E6 503 wallet-service-unavailable.</summary>
    public bool FeeWalletUnresolvable { get; set; }

    public bool SystemWalletUnresolvable { get; set; }

    /// <summary>One-shot fault for the NEXT initiate, so 402-vs-503 mapping is injectable
    /// without a network or a clock. Cleared as it fires.</summary>
    public InitiateFault? FailNextInitiate { get; set; }

    /// <summary>Frozen external reference (DECISION Naming): one ref per offer, shared by its
    /// base + delta holds.</summary>
    public static string OfferReference(string offerId) => $"jeeb:offer:{offerId}";

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
            InitiateCalls++;

            if (FailNextInitiate is { } fault)
            {
                FailNextInitiate = null;
                return Task.FromException<Guid>(FaultFor(fault));
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

    public Task ExecuteAsync(Guid transactionId, CancellationToken ct)
    {
        lock (_gate)
        {
            ExecuteCalls++;
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
            AbortCalls++;
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
