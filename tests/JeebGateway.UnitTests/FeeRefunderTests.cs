using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Financials;
using JeebGateway.Financials.Refunds;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.UnitTests;

// W5/test-2 (L1, DESIGN §2b/§2c): the FeeRefunder idempotency + money-neutrality pins.
// EVERY test runs with CommissionCollection:Enabled=false — the refund decision is the LEDGER, never the flag.
public class FeeRefunderTests
{
    // Frozen naming, CONTRACT §4: asserted as literals so a rename cannot pass silently.
    private const string CaptureTag = "platform-fee";
    private const string RefundTag = "platform-fee-refund";

    private const string StateOpen = "open";
    private const string StateConflict = "conflict";
    private const string StateClosed = "closed";

    private const string JeeberId = "44444444-4444-4444-4444-444444444444";

    private static readonly Guid JeeberWallet = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SystemWallet = new("22222222-2222-2222-2222-222222222222");

    // Never the capture's legs: a refunder that RE-RESOLVES wallets lands on these and the pin fails.
    private static readonly Guid DecoyFeeWallet = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DecoySystemWallet = new("55555555-5555-5555-5555-555555555555");

    private static readonly Guid CaptureTxId = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid PriorRefundTxId = new("77777777-7777-7777-7777-777777777777");

    private static readonly DateTimeOffset Now = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refund_DoubleCancel_CreditsExactlyOnce()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-double", 12.50m);

        await refunder.RefundOnCancelAsync("req-double", JeeberId, "client", CancellationToken.None);
        await refunder.RefundOnCancelAsync("req-double", JeeberId, "client", CancellationToken.None);

        // The second pass reads the executed refund the first pass left in the ledger and mutates nothing.
        Assert.Single(wallet.Initiates);
        Assert.Single(wallet.Executes);
        Assert.Empty(wallet.Aborts);
        Assert.Equal(Key("req-double"), wallet.Initiates[0].IdempotencyKey);
        Assert.Equal(12.50m, wallet.Initiates[0].Amount);
        Assert.Contains(wallet.Ledger(Ref("req-double")), e => e.Tag == RefundTag && e.Status == "Executed");
        Assert.True(intents.IsClosed("req-double"));
    }

    [Fact]
    public async Task Refund_ReplaySeesExistingRefundTag_NoSecondInitiate()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-replay", 8.00m);
        wallet.Seed(Ref("req-replay"), new FeeLedgerEntry(
            PriorRefundTxId, RefundTag, "Executed", 8.00m, SystemWallet, JeeberWallet));
        intents.Seed(OpenIntent("req-replay", 8.00m));

        await refunder.RefundOnCancelAsync("req-replay", JeeberId, "client", CancellationToken.None);

        Assert.Equal(0, wallet.MutationCount);
        Assert.True(intents.IsClosed("req-replay"));
        Assert.Null(await intents.GetAsync("req-replay", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_AmountIsCapturedAmount_NotRecomputed()
    {
        var (refunder, wallet, _) = Build();
        // 7.77 is not 10% of any fee the refunder could recompute; the legs are the capture's, swapped.
        SeedCapture(wallet, "req-amount", 7.77m);

        await refunder.RefundOnCancelAsync("req-amount", JeeberId, "jeeber", CancellationToken.None);

        var initiate = Assert.Single(wallet.Initiates);
        Assert.Equal(7.77m, initiate.Amount);
        Assert.Equal(SystemWallet, initiate.Source);
        Assert.Equal(JeeberWallet, initiate.Destination);
        Assert.Equal(RefundTag, initiate.Tag);
        Assert.Equal(Ref("req-amount"), initiate.ExternalReference);
        Assert.Equal(Key("req-amount"), initiate.IdempotencyKey);
    }

    [Fact]
    public async Task Refund_FlagOff_NoCapture_MakesNoWalletMutationCalls()
    {
        var (refunder, wallet, intents) = Build();

        await refunder.RefundOnCancelAsync("req-flagoff", JeeberId, "client", CancellationToken.None);

        // THE money-neutrality pin: an empty ledger means nothing was captured, so nothing may move.
        Assert.Empty(wallet.Initiates);
        Assert.Empty(wallet.Executes);
        Assert.Empty(wallet.Aborts);
        Assert.Null(await intents.GetAsync("req-flagoff", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_ExecuteAmbiguous_NotAborted_IntentStaysOpen()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-ambiguous", 5.00m);
        wallet.ExecuteFault = Ambiguous();

        await refunder.RefundOnCancelAsync("req-ambiguous", JeeberId, "admin", CancellationToken.None);

        // Aborting a possibly-committed execute is the double-move bug; the sweeper replays instead.
        Assert.Single(wallet.Initiates);
        Assert.Single(wallet.Executes);
        Assert.Empty(wallet.Aborts);
        Assert.Equal(StateOpen, Latest(intents, "req-ambiguous").State);
    }

    [Fact]
    public async Task Refund_IdempotencyConflict_MarksConflict_NoCredit()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-conflict", 3.30m);
        wallet.InitiateFault = Problem(
            HttpStatusCode.Conflict, WalletCommissionDebitException.IdempotencyConflictType);

        await refunder.RefundOnCancelAsync("req-conflict", JeeberId, "client", CancellationToken.None);

        Assert.Empty(wallet.Executes);
        Assert.Empty(wallet.Aborts);
        Assert.DoesNotContain(wallet.Ledger(Ref("req-conflict")), e => e.Tag == RefundTag);
        // Reported every sweep, never blind-retried.
        Assert.Equal(StateConflict, Latest(intents, "req-conflict").State);
    }

    [Fact]
    public async Task Refund_DeterministicExecuteRejection_AbortsPendingLeg_IntentOpen()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-rejected", 4.20m);
        wallet.ExecuteFault = Problem(HttpStatusCode.UnprocessableEntity, problemType: null);

        await refunder.RefundOnCancelAsync("req-rejected", JeeberId, "client", CancellationToken.None);

        var initiate = Assert.Single(wallet.Initiates);
        Assert.Single(wallet.Executes);
        // Money did not move, so the pending header is released and the intent stays retryable.
        Assert.Equal(new[] { wallet.TxIdFor(initiate) }, wallet.Aborts);
        Assert.DoesNotContain(wallet.Ledger(Ref("req-rejected")), e => e.Tag == RefundTag && e.IsExecuted);
        Assert.Equal(StateOpen, Latest(intents, "req-rejected").State);
    }

    [Fact]
    public async Task Refund_LedgerReadFails_WritesOpenIntent_NoMutation()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-unreadable", 6.60m);
        wallet.LedgerReadFault = Ambiguous();

        await refunder.RefundOnCancelAsync("req-unreadable", JeeberId, "client", CancellationToken.None);

        Assert.Equal(0, wallet.MutationCount);
        // Undecidable, so nothing moves — but the sweeper must be able to find it.
        var intent = Latest(intents, "req-unreadable");
        Assert.Equal(StateOpen, intent.State);
        Assert.Equal("req-unreadable", intent.RequestId);
    }

    [Fact]
    public async Task Refund_CallerTokenAborted_NeverThrows_CountsInsteadOfVanishing()
    {
        var (refunder, wallet, _) = Build();
        SeedCapture(wallet, "req-aborted", 2.50m);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        wallet.LedgerReadFault = new OperationCanceledException(cts.Token);

        // W5-F1: "Never throws" is the interface's own contract — an abort must land in the counted
        // ERROR path, not escape the seam untraced.
        await refunder.RefundOnCancelAsync("req-aborted", JeeberId, "client", cts.Token);

        Assert.Equal(0, wallet.MutationCount);
    }

    [Fact]
    public async Task TryRetry_OpenIntent_CompletesCredit_ClosesIntent_ReturnsTrue()
    {
        var (refunder, wallet, intents) = Build();
        SeedCapture(wallet, "req-retry", 9.99m);
        var deferred = OpenIntent("req-retry", 0m);
        intents.Seed(deferred);

        var completed = await refunder.TryRetryAsync(deferred, CancellationToken.None);

        Assert.True(completed);
        var initiate = Assert.Single(wallet.Initiates);
        // The retry re-reads the ledger: the amount is the capture's, never the deferred record's 0.
        Assert.Equal(9.99m, initiate.Amount);
        Assert.Equal(Key("req-retry"), initiate.IdempotencyKey);
        Assert.Single(wallet.Executes);
        Assert.True(intents.IsClosed("req-retry"));
    }

    // ── harness ──

    private static (FeeRefunder Refunder, RecordingWalletClient Wallet, InMemoryRefundIntentStore Intents) Build()
    {
        var wallet = new RecordingWalletClient();
        var intents = new InMemoryRefundIntentStore();
        var refunder = new FeeRefunder(
            wallet,
            intents,
            Options.Create(new CommissionCollectionOptions { Enabled = false }),
            new FakeTimeProvider(Now),
            NullLogger<FeeRefunder>.Instance);
        return (refunder, wallet, intents);
    }

    private static string Ref(string requestId) => "delivery:" + requestId;

    private static string Key(string requestId) => "refund:" + requestId;

    private static void SeedCapture(RecordingWalletClient wallet, string requestId, decimal amount) =>
        wallet.Seed(Ref(requestId), new FeeLedgerEntry(
            CaptureTxId, CaptureTag, "Executed", amount, JeeberWallet, SystemWallet));

    private static RefundIntent OpenIntent(string requestId, decimal amount) =>
        new(requestId, JeeberId, amount, "client", Now, null, StateOpen);

    private static RefundIntent Latest(InMemoryRefundIntentStore intents, string requestId) =>
        intents.Latest(requestId) ?? throw new InvalidOperationException(
            $"no refund intent was recorded for '{requestId}'.");

    private static WalletCommissionDebitException Ambiguous() =>
        new("wallet-service transport fault.", statusCode: null);

    private static WalletCommissionDebitException Problem(HttpStatusCode status, string? problemType) =>
        new($"wallet-service refused (HTTP {(int)status}).", status, problemType);

    /// <summary>Scriptable wallet double: the ledger is real state (initiate appends Pending, execute
    /// flips Executed, abort flips Aborted) so replay pins exercise the true read-then-decide loop.</summary>
    private sealed class RecordingWalletClient : IWalletCommissionDebitClient
    {
        private readonly Dictionary<string, List<FeeLedgerEntry>> _ledger = new(StringComparer.Ordinal);

        public List<InitiateCall> Initiates { get; } = new();

        public List<Guid> Executes { get; } = new();

        public List<Guid> Aborts { get; } = new();

        public Exception? LedgerReadFault { get; set; }

        public Exception? InitiateFault { get; set; }

        public Exception? ExecuteFault { get; set; }

        public int MutationCount => Initiates.Count + Executes.Count + Aborts.Count;

        public void Seed(string externalReference, FeeLedgerEntry entry)
        {
            if (!_ledger.TryGetValue(externalReference, out var entries))
            {
                entries = new List<FeeLedgerEntry>();
                _ledger[externalReference] = entries;
            }

            entries.Add(entry);
        }

        public IReadOnlyList<FeeLedgerEntry> Ledger(string externalReference) =>
            _ledger.TryGetValue(externalReference, out var entries)
                ? entries.ToList()
                : Array.Empty<FeeLedgerEntry>();

        /// <summary>The synthetic id this fake handed back for a recorded initiate.</summary>
        public Guid TxIdFor(InitiateCall call) => SyntheticTxId(Initiates.IndexOf(call) + 1);

        public Task<Guid?> ResolveFeeWalletAsync(Guid holderId, CancellationToken ct) =>
            Task.FromResult<Guid?>(DecoyFeeWallet);

        public Task<Guid?> ResolveSystemWalletAsync(CancellationToken ct) =>
            Task.FromResult<Guid?>(DecoySystemWallet);

        public Task<Guid> InitiateAsync(
            Guid sourceWalletId, Guid destinationWalletId, decimal amount,
            string tag, string notes, string idempotencyKey, string externalReference, CancellationToken ct) =>
            InitiateAsync(
                sourceWalletId, destinationWalletId, amount, tag, notes, idempotencyKey, externalReference,
                isAdditionalFees: true, ct);

        public Task<Guid> InitiateAsync(
            Guid sourceWalletId, Guid destinationWalletId, decimal amount,
            string tag, string notes, string idempotencyKey, string externalReference,
            bool isAdditionalFees, CancellationToken ct)
        {
            Initiates.Add(new InitiateCall(
                sourceWalletId, destinationWalletId, amount, tag, notes, idempotencyKey,
                externalReference, isAdditionalFees));

            if (InitiateFault is not null) return Task.FromException<Guid>(InitiateFault);

            var txId = SyntheticTxId(Initiates.Count);
            Seed(externalReference, new FeeLedgerEntry(
                txId, tag, "Pending", amount, sourceWalletId, destinationWalletId));
            return Task.FromResult(txId);
        }

        public Task<Guid?> FindByExternalReferenceAsync(string externalReference, CancellationToken ct)
        {
            var first = Ledger(externalReference).FirstOrDefault();
            return Task.FromResult<Guid?>(first.TxId == Guid.Empty ? null : first.TxId);
        }

        public Task<IReadOnlyList<HoldHeader>> ListByExternalReferenceAsync(
            string externalReference, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HoldHeader>>(
                Ledger(externalReference).Select(e => new HoldHeader(e.TxId, e.Status, e.Amount)).ToList());

        public Task<IReadOnlyList<FeeLedgerEntry>> ListFeeLedgerByExternalReferenceAsync(
            string externalReference, CancellationToken ct) =>
            LedgerReadFault is not null
                ? Task.FromException<IReadOnlyList<FeeLedgerEntry>>(LedgerReadFault)
                : Task.FromResult(Ledger(externalReference));

        public Task ExecuteAsync(Guid transactionId, CancellationToken ct)
        {
            Executes.Add(transactionId);
            if (ExecuteFault is not null) return Task.FromException(ExecuteFault);

            SetStatus(transactionId, "Executed");
            return Task.CompletedTask;
        }

        public Task AbortAsync(Guid transactionId, CancellationToken ct)
        {
            Aborts.Add(transactionId);
            SetStatus(transactionId, "Aborted");
            return Task.CompletedTask;
        }

        private void SetStatus(Guid txId, string status)
        {
            foreach (var entries in _ledger.Values)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i].TxId == txId) entries[i] = entries[i] with { Status = status };
                }
            }
        }

        private static Guid SyntheticTxId(int ordinal) =>
            Guid.Parse($"00000000-0000-0000-0000-{ordinal:D12}");

        public sealed record InitiateCall(
            Guid Source,
            Guid Destination,
            decimal Amount,
            string Tag,
            string Notes,
            string IdempotencyKey,
            string ExternalReference,
            bool IsAdditionalFees);
    }

    /// <summary>In-memory twin of the state-service store: <c>closed</c> is a tombstone that reads as
    /// ABSENT, and a write carrying the closed state closes too (either close path is honoured).</summary>
    private sealed class InMemoryRefundIntentStore : IRefundIntentStore
    {
        private readonly Dictionary<string, RefundIntent> _latest = new(StringComparer.Ordinal);
        private readonly HashSet<string> _closed = new(StringComparer.Ordinal);

        public List<RefundIntent> Writes { get; } = new();

        public Task WriteAsync(RefundIntent intent, CancellationToken ct)
        {
            Writes.Add(intent);
            _latest[intent.RequestId] = intent;

            if (string.Equals(intent.State, StateClosed, StringComparison.Ordinal)) _closed.Add(intent.RequestId);
            else _closed.Remove(intent.RequestId);

            return Task.CompletedTask;
        }

        public Task<RefundIntent?> GetAsync(string requestId, CancellationToken ct)
        {
            if (_closed.Contains(requestId) || !_latest.TryGetValue(requestId, out var intent))
            {
                return Task.FromResult<RefundIntent?>(null);
            }

            return Task.FromResult<RefundIntent?>(intent);
        }

        public Task<IReadOnlyList<RefundIntent>> ListAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RefundIntent>>(
                _latest.Where(pair => !_closed.Contains(pair.Key)).Select(pair => pair.Value).ToList());

        public Task CloseAsync(string requestId, CancellationToken ct)
        {
            _closed.Add(requestId);
            if (_latest.TryGetValue(requestId, out var intent))
            {
                _latest[requestId] = intent with { State = StateClosed };
            }

            return Task.CompletedTask;
        }

        public void Seed(RefundIntent intent)
        {
            _latest[intent.RequestId] = intent;
            _closed.Remove(intent.RequestId);
        }

        public bool IsClosed(string requestId) => _closed.Contains(requestId);

        public RefundIntent? Latest(string requestId) =>
            _latest.TryGetValue(requestId, out var intent) ? intent : null;
    }
}
