using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Financials;
using JeebGateway.JeebWallet;
using Microsoft.Extensions.Logging;

namespace WalletCurrencyMigration;

/// <summary>
/// OD-C3-3 — owner-run currency-1 (Credit) to USD(2) migration. Wallet HTTP API only: this tool
/// opens no database connection, defaults to dry-run, and re-reads every balance on every run.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        Options options;
        Census census;
        try
        {
            options = Options.Parse(args);
            census = Census.Load(options.CensusPath);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(console => console.SingleLine = true));
        using var walletHttp = new HttpClient
        {
            BaseAddress = new Uri(options.WalletBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var runner = new MigrationRunner(options, census, new WalletApi(walletHttp));

        MigrationSummary summary;
        try
        {
            summary = await runner.RunAsync(CancellationToken.None);
        }
        catch (CensusDriftException ex)
        {
            loggerFactory.CreateLogger("WalletCurrencyMigration").LogError(
                "Census drift before any holder was processed: {Reason}", ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("WalletCurrencyMigration").LogError(
                ex, "Migration aborted during pre-flight; no holder was touched.");
            return 1;
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(summary, Json));
        if (summary.Errors > 0) return 1;
        if (summary.CensusDrift > 0) return 3;
        return 0;
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        $"""
        Usage: WalletCurrencyMigration
          --wallet-base-url <WALLET_SERVICE_URL>
          [--census <PATH>]            (default {Census.DefaultFileName}, committed beside the tool)
          [--dry-run]                  (DEFAULT)
          [--execute --confirm {Options.Confirmation}]
          [--deactivate-drained]

        Default mode is dry-run: currencies, system wallets and every census holder's live balances
        are read, the two-leg transaction shape is probed with POST Transaction/validate, and there
        are ZERO wallet writes - not even holder/ensure. Execute mode mints the missing target
        currency wallet, then posts one initiate+execute per holder under idempotency key
        {MigrationRunner.IdempotencyKeyPrefix}<holderId>. A holder whose live source balance is
        already zero is skipped, which is what makes re-runs no-ops.
        Exit codes: 0 clean, 1 errors, 2 usage, 3 census drift (live balance or system wallet id
        disagrees with the committed census - re-census before executing).
        """);
}

public sealed class Options
{
    public const string Confirmation = "currency-one-usd-migration";

    public required string WalletBaseUrl { get; init; }
    public required string CensusPath { get; init; }
    public bool Execute { get; init; }
    public bool DeactivateDrained { get; init; }

    public string Mode => Execute ? "execute" : "dry-run";

    public static Options Parse(string[] args)
    {
        string? walletBaseUrl = null;
        string? censusPath = null;
        string? confirmation = null;
        var execute = false;
        var deactivateDrained = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wallet-base-url": walletBaseUrl = Value(args, ++i); break;
                case "--census": censusPath = Value(args, ++i); break;
                case "--execute": execute = true; break;
                case "--dry-run": execute = false; break;
                case "--confirm": confirmation = Value(args, ++i); break;
                case "--deactivate-drained": deactivateDrained = true; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (string.IsNullOrWhiteSpace(walletBaseUrl)
            || !Uri.TryCreate(walletBaseUrl, UriKind.Absolute, out var walletUri)
            || walletUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--wallet-base-url must be an absolute HTTP(S) URL");
        }
        if (execute && !string.Equals(confirmation, Confirmation, StringComparison.Ordinal))
            throw new ArgumentException($"--execute requires '--confirm {Confirmation}'");

        return new Options
        {
            WalletBaseUrl = walletBaseUrl,
            CensusPath = censusPath ?? Census.ResolveDefaultPath(),
            Execute = execute,
            DeactivateDrained = deactivateDrained,
        };
    }

    private static string Value(string[] args, int index) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException("missing value for the preceding argument");
}

/// <summary>The committed W0 census: holder ids plus the balances they carried when it was taken.
/// Balances are re-read live every run; the census values only detect drift.</summary>
public sealed class Census
{
    public const string DefaultFileName = "census-2026-08-24.json";

    public int SourceCurrencyId { get; set; }
    public int TargetCurrencyId { get; set; }
    public Guid SystemCcy1WalletId { get; set; }
    public List<CensusHolder> Holders { get; set; } = new();

    public static string ResolveDefaultPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        return File.Exists(beside) ? beside : Path.Combine(Environment.CurrentDirectory, DefaultFileName);
    }

    public static Census Load(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"census file '{path}' does not exist");

        Census? census;
        try
        {
            census = JsonSerializer.Deserialize<Census>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"census file '{path}' is not valid JSON: {ex.Message}");
        }

        if (census is null || census.SourceCurrencyId <= 0 || census.TargetCurrencyId <= 0)
            throw new ArgumentException("census must carry a positive sourceCurrencyId and targetCurrencyId");
        if (census.SourceCurrencyId == census.TargetCurrencyId)
            throw new ArgumentException("census sourceCurrencyId and targetCurrencyId must differ");
        if (census.SystemCcy1WalletId == Guid.Empty)
            throw new ArgumentException("census must carry the systemCcy1WalletId retirement sink");
        if (census.Holders.Count == 0)
            throw new ArgumentException("census carries no holders");
        if (census.Holders.Any(holder => holder.HolderId == Guid.Empty))
            throw new ArgumentException("census holderId must never be the system holder Guid.Empty");
        if (census.Holders.GroupBy(holder => holder.HolderId).Any(group => group.Count() != 1))
            throw new ArgumentException("census holder ids must be unique");

        return census;
    }
}

public sealed class CensusHolder
{
    public Guid HolderId { get; set; }
    public string HolderName { get; set; } = string.Empty;
    public decimal ExpectedCcy1 { get; set; }
}

/// <summary>Live wallet state disagrees with the committed census; the owner must re-census.</summary>
public sealed class CensusDriftException : Exception
{
    public CensusDriftException(string message) : base(message)
    {
    }
}

public sealed class MigrationRunner
{
    public const string IdempotencyKeyPrefix = "ccy1-migration:";
    public const string TransactionTag = "ccy1-usd-migration";
    public const string ServiceName = "jeeb-gateway-tools";

    private const string SystemWalletType = "__SYSTEM__";
    private const string SystemPrimaryWalletType = "__SYSTEM__PRIMARY__";
    private const string MintedWalletType = "jeeb";
    private const string MintedWalletNote = "ccy1-usd-migration";
    private const string MintedHolderType = "jeeber";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Options _options;
    private readonly Census _census;
    private readonly IWalletApi _wallet;
    private readonly Action<object> _write;

    public MigrationRunner(Options options, Census census, IWalletApi wallet, Action<object>? write = null)
    {
        _options = options;
        _census = census;
        _wallet = wallet;
        _write = write ?? Write;
    }

    public async Task<MigrationSummary> RunAsync(CancellationToken ct)
    {
        var currencies = await _wallet.ReadCurrenciesAsync(ct);
        var source = currencies.FirstOrDefault(currency => currency.Id == _census.SourceCurrencyId)
            ?? throw new InvalidOperationException(
                $"wallet-service does not configure source currency id {_census.SourceCurrencyId}.");
        var target = currencies.FirstOrDefault(currency => currency.Id == _census.TargetCurrencyId)
            ?? throw new InvalidOperationException(
                $"wallet-service does not configure target currency id {_census.TargetCurrencyId}.");
        if (source.Rate <= 0m || target.Rate <= 0m)
            throw new InvalidOperationException("wallet-service currency rates must be positive.");

        // Read from the live catalog, never hardcoded: this is the same ratio ConvertCurrency applies.
        var rate = source.Rate / target.Rate;

        var systemWallets = await _wallet.ReadSystemWalletsAsync(ct);
        var sink = systemWallets.SingleOrDefault(wallet =>
            wallet.IsActive && wallet.CurrencyId == source.Id
            && string.Equals(wallet.Type, SystemWalletType, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"no active {SystemWalletType} wallet in currency {source.Id} to retire the balance into.");
        var funding = systemWallets.SingleOrDefault(wallet =>
            wallet.IsActive && wallet.CurrencyId == target.Id
            && string.Equals(wallet.Type, SystemPrimaryWalletType, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"no active {SystemPrimaryWalletType} wallet in currency {target.Id} to fund the USD issuance.");
        if (sink.WalletId != _census.SystemCcy1WalletId)
        {
            throw new CensusDriftException(
                $"live system currency-{source.Id} wallet is {sink.WalletId:D} but the census expects "
                + $"{_census.SystemCcy1WalletId:D}.");
        }

        var summary = new MigrationSummary
        {
            Mode = _options.Mode,
            SourceCurrencyId = source.Id,
            SourceCurrencyCode = source.Code,
            TargetCurrencyId = target.Id,
            TargetCurrencyCode = target.Code,
            ConversionRate = rate,
            HoldersScanned = _census.Holders.Count,
        };

        _write(new CatalogRow(
            "currency_catalog", summary.Mode, source.Id, source.Code, source.Rate,
            target.Id, target.Code, target.Rate, rate, sink.WalletId, funding.WalletId));

        var plans = new List<HolderPlan>();
        foreach (var holder in _census.Holders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                plans.Add(await PlanAsync(holder, source.Id, target.Id, rate, sink, funding, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                plans.Add(HolderPlan.Failed(holder, ex.Message));
            }
        }

        // One run-level probe answers "will wallet-service let the system wallet fund this?" even
        // for the four holders whose USD wallet does not exist yet (nothing to address a leg to).
        var probeSubject = plans.FirstOrDefault(plan => plan.CanProbe);
        summary.SystemSourceProbe = probeSubject is null
            ? "unavailable_no_target_wallet_yet"
            : (await ProbeAsync(new[] { probeSubject.IssueLeg!.Value }, ct) ? "accepted" : "rejected");

        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessAsync(plan, summary, ct);
        }

        return summary;
    }

    private async Task<HolderPlan> PlanAsync(
        CensusHolder holder, int sourceCurrencyId, int targetCurrencyId, decimal rate,
        WalletRow sink, WalletRow funding, CancellationToken ct)
    {
        var live = await _wallet.ReadHolderAsync(holder.HolderId, ct);
        var wallets = live.Wallets ?? Array.Empty<WalletRow>();
        var sources = wallets
            .Where(wallet => wallet.IsActive
                && wallet.CurrencyId == sourceCurrencyId
                && SpendableWalletTypes.IsSpendable(wallet.Type)
                && wallet.Amount > 0m)
            .OrderBy(wallet => wallet.WalletId)
            .ToArray();
        var available = sources.Sum(wallet => wallet.Amount);
        var targets = wallets
            .Where(wallet => wallet.IsActive
                && wallet.CurrencyId == targetCurrencyId
                && SpendableWalletTypes.IsSpendable(wallet.Type))
            .OrderBy(wallet => wallet.WalletId)
            .ToArray();

        var plan = new HolderPlan(holder)
        {
            HolderExists = live.WalletHolder is not null,
            HolderName = live.WalletHolder?.HolderName ?? holder.HolderName,
            HolderType = live.WalletHolder?.HolderType ?? MintedHolderType,
            Available = available,
            SourceWallets = sources,
            TargetWalletId = targets.Length == 1 ? targets[0].WalletId : null,
            TargetWalletState = targets.Length switch
            {
                0 => "missing",
                1 => "present",
                _ => "ambiguous",
            },
            Sink = sink,
            Funding = funding,
            TargetCurrencyId = targetCurrencyId,
        };

        if (targets.Length > 1)
            return plan.WithError("holder has more than one active spendable wallet in the target currency.");
        if (!plan.HolderExists)
            return plan.WithError("wallet-service has no holder record for this census id.");
        if (available == 0m)
        {
            plan.Outcome = "skipped_already_migrated";
            return plan;
        }
        if (available != holder.ExpectedCcy1)
        {
            plan.Outcome = "census_drift";
            return plan;
        }

        // Value-preserving at catalog rates; a balance too small to round to a target-currency unit
        // is never retired, because that would destroy value.
        plan.Credit = decimal.Round(rate * available, 2, MidpointRounding.AwayFromZero);
        if (plan.Credit <= 0m)
            return plan.WithError($"{available} converts to 0 at catalog rate {rate}; nothing is retired.");

        plan.Outcome = "ready_to_migrate";
        return plan;
    }

    private async Task ProcessAsync(HolderPlan plan, MigrationSummary summary, CancellationToken ct)
    {
        var probe = "not_probed";
        var shape = "single-transaction";
        var transactions = new List<string>();
        var deactivated = new List<string>();

        try
        {
            if (plan.Outcome == "error") summary.Errors++;
            if (plan.Outcome == "census_drift") summary.CensusDrift++;
            if (plan.Outcome == "skipped_already_migrated") summary.HoldersSkipped++;

            if (plan.Outcome == "ready_to_migrate")
            {
                probe = plan.CanProbe
                    ? (await ProbeAsync(plan.AllLegs, ct) ? "accepted" : "rejected")
                    : "deferred_target_wallet_missing";
                if (probe == "rejected") shape = "two-transaction-fallback";

                if (_options.Execute)
                {
                    await ExecuteAsync(plan, transactions, summary, ct);
                    plan.Outcome = "migrated";
                    summary.HoldersMigrated++;
                    summary.SourceCurrencyDelta -= plan.Available;
                    summary.TargetCurrencyDelta += plan.Credit;
                }
                else
                {
                    summary.HoldersReady++;
                    summary.SourceCurrencyDelta -= plan.Available;
                    summary.TargetCurrencyDelta += plan.Credit;
                }
            }

            if (_options.DeactivateDrained && plan.Outcome is "migrated" or "skipped_already_migrated")
                await DeactivateAsync(plan, deactivated, summary, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            plan.Error = ex.Message;
            plan.Outcome = "error";
            summary.Errors++;
        }

        _write(new HolderRow(
            "holder_currency_migration",
            summary.Mode,
            plan.Holder.HolderId.ToString("D"),
            plan.HolderName,
            plan.Holder.ExpectedCcy1,
            plan.Available,
            plan.Credit,
            plan.SourceWallets.Select(wallet => wallet.WalletId.ToString("D")).ToArray(),
            plan.TargetWalletId?.ToString("D"),
            plan.TargetWalletState,
            probe,
            shape,
            transactions,
            deactivated,
            plan.Outcome,
            plan.Error));
    }

    private async Task ExecuteAsync(
        HolderPlan plan, List<string> transactions, MigrationSummary summary, CancellationToken ct)
    {
        if (plan.TargetWalletId is null)
        {
            plan.TargetWalletId = await _wallet.EnsureTargetWalletAsync(
                plan.Holder.HolderId, plan.HolderName, plan.HolderType, plan.TargetCurrencyId,
                MintedWalletType, MintedWalletNote, ct);
            plan.TargetWalletState = "ensured";
            summary.WalletsEnsured++;
        }

        var key = IdempotencyKeyPrefix + plan.Holder.HolderId.ToString("D");
        if (await ProbeAsync(plan.AllLegs, ct))
        {
            transactions.Add(await PostAsync(plan.AllLegs, key, ct));
            return;
        }

        // Documented w0 fallback: wallet-service refused the system-source leg inside the batch, so
        // mint the USD first and retire the Credit second, each under its own idempotency key.
        if (!await ProbeAsync(new[] { plan.IssueLeg!.Value }, ct))
            throw new InvalidOperationException("wallet-service rejects the system-funded issuance leg; nothing was moved.");
        transactions.Add(await PostAsync(new[] { plan.IssueLeg!.Value }, key + ":issue", ct));
        transactions.Add(await PostAsync(plan.RetireLegs, key + ":retire", ct));
    }

    private async Task DeactivateAsync(
        HolderPlan plan, List<string> deactivated, MigrationSummary summary, CancellationToken ct)
    {
        var live = await _wallet.ReadHolderAsync(plan.Holder.HolderId, ct);
        var candidates = (live.Wallets ?? Array.Empty<WalletRow>())
            .Where(wallet => wallet.IsActive
                && wallet.CurrencyId == _census.SourceCurrencyId
                && wallet.Amount == 0m)
            .OrderBy(wallet => wallet.WalletId)
            .ToArray();

        foreach (var wallet in candidates)
        {
            deactivated.Add(wallet.WalletId.ToString("D"));
            if (!_options.Execute) continue;
            await _wallet.DeactivateAsync(plan.Holder.HolderId, wallet.WalletId, ct);
            summary.WalletsDeactivated++;
        }
    }

    private async Task<string> PostAsync(IReadOnlyList<Leg> legs, string idempotencyKey, CancellationToken ct)
    {
        var transactionId = await _wallet.InitiateAsync(legs, ServiceName, TransactionTag, idempotencyKey, ct);
        await _wallet.ExecuteAsync(transactionId, ct);
        return transactionId.ToString("D");
    }

    private Task<bool> ProbeAsync(IReadOnlyList<Leg> legs, CancellationToken ct) =>
        _wallet.ValidateAsync(legs, ct);

    private static void Write(object row) =>
        Console.WriteLine(JsonSerializer.Serialize(row, row.GetType(), Json));

    private sealed class HolderPlan
    {
        public HolderPlan(CensusHolder holder) => Holder = holder;

        public CensusHolder Holder { get; }
        public bool HolderExists { get; init; }
        public string HolderName { get; init; } = string.Empty;
        public string HolderType { get; init; } = string.Empty;
        public decimal Available { get; init; }
        public decimal Credit { get; set; }
        public IReadOnlyList<WalletRow> SourceWallets { get; init; } = Array.Empty<WalletRow>();
        public Guid? TargetWalletId { get; set; }
        public string TargetWalletState { get; set; } = "unknown";
        public int TargetCurrencyId { get; init; }
        public WalletRow Sink { get; init; } = new();
        public WalletRow Funding { get; init; } = new();
        public string Outcome { get; set; } = "error";
        public string? Error { get; set; }

        /// <summary>Leg B of w0 §2: the system primary wallet issues the converted target-currency amount.</summary>
        public Leg? IssueLeg => TargetWalletId is null || Credit <= 0m
            ? null
            : new Leg(Funding.WalletId, TargetWalletId.Value, Credit);

        /// <summary>Leg A of w0 §2: every funded source wallet drains into the system retirement sink.</summary>
        public IReadOnlyList<Leg> RetireLegs => SourceWallets
            .Select(wallet => new Leg(wallet.WalletId, Sink.WalletId, wallet.Amount))
            .ToArray();

        public IReadOnlyList<Leg> AllLegs => IssueLeg is null
            ? RetireLegs
            : RetireLegs.Append(IssueLeg.Value).ToArray();

        public bool CanProbe => Outcome == "ready_to_migrate" && IssueLeg is not null;

        public HolderPlan WithError(string error)
        {
            Outcome = "error";
            Error = error;
            return this;
        }

        public static HolderPlan Failed(CensusHolder holder, string error) =>
            new HolderPlan(holder).WithError(error);
    }
}

public readonly record struct Leg(Guid SourceWalletId, Guid DestinationWalletId, decimal Amount);

public interface IWalletApi
{
    Task<IReadOnlyList<CurrencyRow>> ReadCurrenciesAsync(CancellationToken ct);
    Task<IReadOnlyList<WalletRow>> ReadSystemWalletsAsync(CancellationToken ct);
    Task<HolderWallets> ReadHolderAsync(Guid holderId, CancellationToken ct);
    Task<Guid> EnsureTargetWalletAsync(
        Guid holderId, string holderName, string holderType, int currencyId,
        string walletType, string note, CancellationToken ct);
    Task<bool> ValidateAsync(IReadOnlyList<Leg> legs, CancellationToken ct);
    Task<Guid> InitiateAsync(
        IReadOnlyList<Leg> legs, string serviceName, string tag, string idempotencyKey, CancellationToken ct);
    Task ExecuteAsync(Guid transactionId, CancellationToken ct);
    Task DeactivateAsync(Guid holderId, Guid walletId, CancellationToken ct);
}

/// <summary>The wallet-service surface this migration needs. Hand-rolled like
/// <see cref="WalletCommissionDebitClient"/>, because the generated client carries no idempotency header.</summary>
public sealed class WalletApi : IWalletApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public WalletApi(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<CurrencyRow>> ReadCurrenciesAsync(CancellationToken ct)
    {
        var currencies = await GetAsync<List<CurrencyRow>>("Fees/currencies", ct)
            ?? new List<CurrencyRow>();
        if (currencies.Count == 0
            || currencies.Any(currency => currency.Id <= 0)
            || currencies.GroupBy(currency => currency.Id).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException(
                "wallet-service must expose a non-empty, unambiguous configured currency set.");
        }

        return currencies;
    }

    public async Task<IReadOnlyList<WalletRow>> ReadSystemWalletsAsync(CancellationToken ct)
    {
        // wallet-service maps the system holder at the absolute route /system-wallet, not under /Wallet.
        var system = await GetAsync<HolderWallets>("system-wallet", ct)
            ?? throw new InvalidOperationException("wallet-service returned no system wallet holder.");
        return system.Wallets ?? Array.Empty<WalletRow>();
    }

    public async Task<HolderWallets> ReadHolderAsync(Guid holderId, CancellationToken ct) =>
        await GetAsync<HolderWallets>($"Wallet/holder/{holderId:D}/wallets", ct) ?? new HolderWallets();

    public async Task<Guid> EnsureTargetWalletAsync(
        Guid holderId, string holderName, string holderType, int currencyId,
        string walletType, string note, CancellationToken ct)
    {
        var payload = new EnsureHolderWire(
            new EnsureHolderPayload(holderId, holderName, holderType),
            new[] { new EnsureWalletWire(currencyId, walletType, note) });
        using var response = await _http.PutAsJsonAsync("Wallet/holder/ensure", payload, Json, ct);
        await EnsureSuccessAsync(response, "ensure the target-currency wallet", ct);

        var ensured = await ReadHolderAsync(holderId, ct);
        var wallets = (ensured.Wallets ?? Array.Empty<WalletRow>())
            .Where(wallet => wallet.IsActive
                && wallet.CurrencyId == currencyId
                && SpendableWalletTypes.IsSpendable(wallet.Type))
            .ToArray();
        if (wallets.Length != 1 || wallets[0].WalletId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"holder {holderId:D} did not converge to exactly one active spendable wallet in currency {currencyId}.");
        }

        return wallets[0].WalletId;
    }

    public async Task<bool> ValidateAsync(IReadOnlyList<Leg> legs, CancellationToken ct)
    {
        var body = legs.Select(leg => new LegWire(
            leg.SourceWalletId, leg.DestinationWalletId, leg.Amount, IsAdditionalFees: false)).ToArray();
        using var response = await _http.PostAsJsonAsync("Transaction/validate", body, Json, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest) return false;
        await EnsureSuccessAsync(response, "validate the migration legs", ct);
        return true;
    }

    public async Task<Guid> InitiateAsync(
        IReadOnlyList<Leg> legs, string serviceName, string tag, string idempotencyKey, CancellationToken ct)
    {
        var body = new InitiateWire(
            serviceName,
            tag,
            $"currency migration under {idempotencyKey}",
            // Every accounting leg is supplied explicitly; wallet-service must add no fee legs.
            ApplyConfiguredFees: false,
            legs.Select(leg => new LegWire(
                leg.SourceWalletId, leg.DestinationWalletId, leg.Amount, IsAdditionalFees: false)).ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Post, "Transaction/initiate")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.TryAddWithoutValidation(WalletCommissionDebitClient.IdempotencyHeader, idempotencyKey);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "initiate the migration transaction", ct);

        var transaction = await response.Content.ReadFromJsonAsync<TransactionWire>(Json, ct);
        var transactionId = transaction?.TransactionHeader?.TxId ?? Guid.Empty;
        return transactionId == Guid.Empty
            ? throw new InvalidOperationException("wallet-service returned no transaction id to execute.")
            : transactionId;
    }

    public async Task ExecuteAsync(Guid transactionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"Transaction/{transactionId:D}/execute");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "execute the migration transaction", ct);
    }

    public async Task DeactivateAsync(Guid holderId, Guid walletId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"Wallet/{holderId:D}/{walletId:D}/deactivate");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "deactivate the drained source wallet", ct);
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        await EnsureSuccessAsync(response, $"read {url}", ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"wallet-service could not {operation} (HTTP {(int)response.StatusCode}): "
            + (body.Length <= 300 ? body : body[..300]));
    }

    private sealed record InitiateWire(
        string ServiceName,
        string Tag,
        string Notes,
        bool ApplyConfiguredFees,
        IReadOnlyList<LegWire> Transactions);

    private sealed record LegWire(
        Guid SourceWalletId,
        Guid DestinationWalletId,
        decimal Amount,
        bool IsAdditionalFees);

    private sealed record EnsureHolderWire(
        EnsureHolderPayload WalletHolder,
        IReadOnlyList<EnsureWalletWire> Wallets);

    private sealed record EnsureHolderPayload(Guid HolderId, string HolderName, string HolderType);

    private sealed record EnsureWalletWire(
        [property: JsonPropertyName("currencyID")] int CurrencyId,
        string Type,
        string Note);

    private sealed class TransactionWire
    {
        public TransactionHeaderWire? TransactionHeader { get; set; }
    }

    private sealed class TransactionHeaderWire
    {
        public Guid TxId { get; set; }
    }
}

public sealed class CurrencyRow
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;
    public decimal Rate { get; set; }
}

public sealed class HolderWallets
{
    public HolderRecord? WalletHolder { get; set; }
    public IReadOnlyList<WalletRow>? Wallets { get; set; }
}

public sealed class HolderRecord
{
    public Guid HolderId { get; set; }
    public string HolderName { get; set; } = string.Empty;
    public string HolderType { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class WalletRow
{
    public Guid WalletId { get; set; }

    [JsonPropertyName("currencyID")]
    public int CurrencyId { get; set; }

    public string? Type { get; set; }
    public decimal Amount { get; set; }
    public bool IsActive { get; set; }
}

public sealed record CatalogRow(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("source_currency_id")] int SourceCurrencyId,
    [property: JsonPropertyName("source_currency_code")] string SourceCurrencyCode,
    [property: JsonPropertyName("source_rate")] decimal SourceRate,
    [property: JsonPropertyName("target_currency_id")] int TargetCurrencyId,
    [property: JsonPropertyName("target_currency_code")] string TargetCurrencyCode,
    [property: JsonPropertyName("target_rate")] decimal TargetRate,
    [property: JsonPropertyName("conversion_rate")] decimal ConversionRate,
    [property: JsonPropertyName("system_retirement_wallet_id")] Guid SystemRetirementWalletId,
    [property: JsonPropertyName("system_funding_wallet_id")] Guid SystemFundingWalletId);

public sealed record HolderRow(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("holder_id")] string HolderId,
    [property: JsonPropertyName("holder_name")] string HolderName,
    [property: JsonPropertyName("expected_source_balance")] decimal ExpectedSourceBalance,
    [property: JsonPropertyName("live_source_balance")] decimal LiveSourceBalance,
    [property: JsonPropertyName("target_credit")] decimal TargetCredit,
    [property: JsonPropertyName("source_wallet_ids")] IReadOnlyList<string> SourceWalletIds,
    [property: JsonPropertyName("target_wallet_id")] string? TargetWalletId,
    [property: JsonPropertyName("target_wallet_state")] string TargetWalletState,
    [property: JsonPropertyName("validate_probe")] string ValidateProbe,
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("transaction_ids")] IReadOnlyList<string> TransactionIds,
    [property: JsonPropertyName("deactivated_wallet_ids")] IReadOnlyList<string> DeactivatedWalletIds,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("error")] string? Error);

public sealed class MigrationSummary
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("source_currency_id")]
    public int SourceCurrencyId { get; set; }

    [JsonPropertyName("source_currency_code")]
    public string SourceCurrencyCode { get; set; } = string.Empty;

    [JsonPropertyName("target_currency_id")]
    public int TargetCurrencyId { get; set; }

    [JsonPropertyName("target_currency_code")]
    public string TargetCurrencyCode { get; set; } = string.Empty;

    [JsonPropertyName("conversion_rate")]
    public decimal ConversionRate { get; set; }

    [JsonPropertyName("system_source_probe")]
    public string SystemSourceProbe { get; set; } = "not_probed";

    [JsonPropertyName("holders_scanned")]
    public int HoldersScanned { get; set; }

    [JsonPropertyName("holders_skipped")]
    public int HoldersSkipped { get; set; }

    [JsonPropertyName("holders_ready")]
    public int HoldersReady { get; set; }

    [JsonPropertyName("holders_migrated")]
    public int HoldersMigrated { get; set; }

    [JsonPropertyName("wallets_ensured")]
    public int WalletsEnsured { get; set; }

    [JsonPropertyName("wallets_deactivated")]
    public int WalletsDeactivated { get; set; }

    [JsonPropertyName("census_drift")]
    public int CensusDrift { get; set; }

    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    [JsonPropertyName("source_currency_delta")]
    public decimal SourceCurrencyDelta { get; set; }

    [JsonPropertyName("target_currency_delta")]
    public decimal TargetCurrencyDelta { get; set; }
}
