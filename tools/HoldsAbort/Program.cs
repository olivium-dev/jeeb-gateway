using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoldsAbort;

/// <summary>R4 break-glass — releases every outstanding jeeb-gateway offer hold over the wallet +
/// state HTTP APIs. Dry-run by default; nothing is aborted or tombstoned without --execute.</summary>
public static class Program
{
    /// <summary>The gateway's durable intent-record key space (DECISION "Naming (frozen)").</summary>
    public const string IntentKeyPrefix = "wgf:hold:";

    /// <summary>One external reference per offer, shared by its base + raise-delta holds.</summary>
    public const string ExternalReferencePrefix = "jeeb:offer:";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            Options.PrintUsage();
            return 2;
        }

        var ct = CancellationToken.None;
        using var http = new HttpClientPool();
        var state = new StateApi(http.Create(options.StateUrl, options.StateToken));
        var wallet = new WalletApi(http.Create(options.WalletUrl, options.WalletToken));

        var report = new AbortReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Mode = options.Mode,
            WalletUrl = options.WalletUrl,
            StateUrl = options.StateUrl,
            JeeberFilter = options.Jeeber,
        };

        IReadOnlyList<IdempotencyRecordWire> records;
        try
        {
            records = await state.ListByPrefixAsync(IntentKeyPrefix, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not enumerate '{IntentKeyPrefix}*' intent records: {ex.Message}");
            return 1;
        }

        report.RecordsScanned = records.Count;

        var intents = new List<HoldIntent>();
        foreach (var record in records)
        {
            HoldIntent intent;
            try
            {
                intent = HoldIntent.Parse(record);
            }
            catch (Exception ex)
            {
                report.Errors.Add(new ItemError(record.Key ?? "(no key)", $"unreadable intent: {ex.Message}"));
                continue;
            }

            if (intent.IsClosed)
            {
                report.AlreadyTombstoned.Add(intent.Key);
                continue;
            }
            if (!options.Matches(intent.JeeberId))
            {
                report.FilteredOut.Add(intent.Key);
                continue;
            }

            intents.Add(intent);
        }

        // Netted balances BEFORE, per holder: releasing a pending outgoing leg is the only thing
        // that can move this number here, so it is the verification baseline (step 3).
        var before = await ReadHolderBalancesAsync(wallet, intents, report, ct);

        foreach (var intent in intents)
        {
            var plan = new HoldPlan
            {
                Key = intent.Key,
                OfferId = intent.OfferId,
                JeeberId = intent.JeeberId,
                RequestId = intent.RequestId,
                State = intent.State,
                ExpectedAmount = intent.ExpectedAmount,
                PlacedAtUtc = intent.PlacedAtUtc,
                ExternalReference = intent.ExternalReference,
            };
            report.Plans.Add(plan);

            IReadOnlyList<HoldHeader> headers;
            try
            {
                headers = await wallet.ListByExternalReferenceAsync(intent.ExternalReference, ct);
            }
            catch (Exception ex)
            {
                plan.Outcome = "read-failed";
                report.Errors.Add(new ItemError(intent.Key, $"by-external-reference read failed: {ex.Message}"));
                continue;
            }

            plan.Headers = headers.Select(header => new HoldHeaderView(
                header.TxId.ToString("D"), header.StatusName, header.Amount)).ToList();
            plan.PendingTotal = headers.Where(header => header.IsPending).Sum(header => header.Amount);

            var pending = headers.Where(header => header.IsPending).ToArray();
            if (pending.Length == 0)
            {
                plan.Outcome = options.Execute ? "stale-record-tombstoned" : "would-tombstone-stale-record";
                if (options.Execute) await TombstoneAsync(state, intent, plan, report, ct);
                continue;
            }

            if (!options.Execute)
            {
                plan.Outcome = $"would-abort-{pending.Length}-pending-then-tombstone";
                continue;
            }

            var aborted = 0m;
            var blocked = false;
            foreach (var header in pending)
            {
                var outcome = await wallet.AbortAsync(header.TxId, ct);
                switch (outcome.Kind)
                {
                    case AbortKind.Aborted:
                        plan.AbortedTxIds.Add(header.TxId.ToString("D"));
                        aborted += header.Amount;
                        break;
                    case AbortKind.AlreadyExecuted:
                        // wallet-service answers abort-after-execute with a 500 it can never undo.
                        // Report it and move on: a blind retry would only repeat the same 500.
                        plan.AlreadyExecutedTxIds.Add(header.TxId.ToString("D"));
                        report.Errors.Add(new ItemError(
                            intent.Key,
                            $"tx {header.TxId:D} was ALREADY EXECUTED — money moved, skipped (not retried). "
                            + "Reconcile this offer by hand."));
                        blocked = true;
                        break;
                    default:
                        report.Errors.Add(new ItemError(
                            intent.Key, $"abort of tx {header.TxId:D} failed: {outcome.Detail}"));
                        blocked = true;
                        break;
                }
            }

            plan.AbortedTotal = aborted;

            IReadOnlyList<HoldHeader> after;
            try
            {
                after = await wallet.ListByExternalReferenceAsync(intent.ExternalReference, ct);
            }
            catch (Exception ex)
            {
                plan.Outcome = "verify-failed";
                report.Errors.Add(new ItemError(intent.Key, $"post-abort re-read failed: {ex.Message}"));
                continue;
            }

            var stillPending = after.Count(header => header.IsPending);
            if (stillPending > 0 || blocked)
            {
                // Never tombstone a record whose holds are not provably gone: the record IS the
                // sweeper's only handle on them (DECISION I2).
                plan.Outcome = stillPending > 0 ? $"incomplete-{stillPending}-still-pending" : "incomplete-blocked";
                continue;
            }

            plan.Outcome = "released";
            await TombstoneAsync(state, intent, plan, report, ct);
        }

        report.ReleasedTotal = report.Plans.Sum(plan => plan.AbortedTotal);
        await VerifyBalancesAsync(wallet, before, report, ct);

        var path = Path.Combine(
            Environment.CurrentDirectory,
            $"holdsabort-{report.Mode}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json), ct);

        PrintPlan(report, path);
        return report.Errors.Count > 0 ? 1 : 0;
    }

    private static async Task TombstoneAsync(
        StateApi state, HoldIntent intent, HoldPlan plan, AbortReport report, CancellationToken ct)
    {
        try
        {
            await state.TombstoneAsync(intent, ct);
            plan.Tombstoned = true;
        }
        catch (Exception ex)
        {
            report.Errors.Add(new ItemError(intent.Key, $"tombstone write failed: {ex.Message}"));
        }
    }

    /// <summary>Netted (pending-subtracted) balances per holder before anything is aborted. A holder
    /// whose id is not a GUID has no wallet to read, which is reported rather than assumed away.</summary>
    private static async Task<Dictionary<Guid, Dictionary<Guid, decimal>>> ReadHolderBalancesAsync(
        WalletApi wallet, IReadOnlyList<HoldIntent> intents, AbortReport report, CancellationToken ct)
    {
        var balances = new Dictionary<Guid, Dictionary<Guid, decimal>>();
        foreach (var jeeberId in intents.Select(intent => intent.JeeberId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(jeeberId, out var holderId))
            {
                report.Errors.Add(new ItemError(jeeberId, "jeeberId is not a GUID — balance verification skipped"));
                continue;
            }
            if (balances.ContainsKey(holderId)) continue;

            try
            {
                balances[holderId] = await wallet.ReadNettedWalletsAsync(holderId, ct);
            }
            catch (Exception ex)
            {
                report.Errors.Add(new ItemError(jeeberId, $"holder wallet read failed: {ex.Message}"));
            }
        }

        return balances;
    }

    /// <summary>Step 3 of the runbook: once every pending outgoing leg is gone the netted read
    /// equals the gross (settled) balance, so it must have risen by exactly what was released.</summary>
    private static async Task VerifyBalancesAsync(
        WalletApi wallet,
        Dictionary<Guid, Dictionary<Guid, decimal>> before,
        AbortReport report,
        CancellationToken ct)
    {
        foreach (var (holderId, snapshot) in before)
        {
            var expected = report.Plans
                .Where(plan => Guid.TryParse(plan.JeeberId, out var planHolder) && planHolder == holderId)
                .Sum(plan => plan.AbortedTotal);

            Dictionary<Guid, decimal> after;
            try
            {
                after = await wallet.ReadNettedWalletsAsync(holderId, ct);
            }
            catch (Exception ex)
            {
                report.Errors.Add(new ItemError(holderId.ToString("D"), $"verification read failed: {ex.Message}"));
                continue;
            }

            // Only wallets present in BOTH snapshots are compared: a wallet that appeared or
            // vanished mid-run is somebody else's write, not a hold this tool released.
            var released = snapshot
                .Where(entry => after.ContainsKey(entry.Key))
                .Sum(entry => after[entry.Key] - entry.Value);
            var verification = new BalanceVerification(
                holderId.ToString("D"), expected, released, Matches: released == expected);
            report.Balances.Add(verification);

            if (!verification.Matches)
            {
                report.Errors.Add(new ItemError(
                    holderId.ToString("D"),
                    $"netted balance rose by {released.ToString(CultureInfo.InvariantCulture)} but "
                    + $"{expected.ToString(CultureInfo.InvariantCulture)} was released — another writer "
                    + "moved this holder's money during the run, or a hold is still pending."));
            }
        }
    }

    private static void PrintPlan(AbortReport report, string path)
    {
        Console.WriteLine($"# holds-abort {report.Mode} — {report.GeneratedAt:u}");
        Console.WriteLine($"# wallet={report.WalletUrl} state={report.StateUrl} "
            + $"jeeber={report.JeeberFilter ?? "(all)"}");
        Console.WriteLine($"# records scanned={report.RecordsScanned} actionable={report.Plans.Count} "
            + $"already-tombstoned={report.AlreadyTombstoned.Count} filtered-out={report.FilteredOut.Count}");
        Console.WriteLine();

        foreach (var plan in report.Plans)
        {
            Console.WriteLine($"{plan.Key}");
            Console.WriteLine($"  offer={plan.OfferId} jeeber={plan.JeeberId} request={plan.RequestId} "
                + $"state={plan.State} placed={plan.PlacedAtUtc:u}");
            Console.WriteLine($"  externalReference={plan.ExternalReference} "
                + $"expected={plan.ExpectedAmount.ToString(CultureInfo.InvariantCulture)} "
                + $"pendingTotal={plan.PendingTotal.ToString(CultureInfo.InvariantCulture)}");
            foreach (var header in plan.Headers)
            {
                Console.WriteLine($"    tx {header.TxId} status={header.Status} "
                    + $"amount={header.Amount.ToString(CultureInfo.InvariantCulture)}");
            }

            Console.WriteLine($"  => {plan.Outcome}{(plan.Tombstoned ? " + record tombstoned" : string.Empty)}");
        }

        foreach (var balance in report.Balances)
        {
            Console.WriteLine($"balance {balance.HolderId}: released="
                + $"{balance.Released.ToString(CultureInfo.InvariantCulture)} expected="
                + $"{balance.Expected.ToString(CultureInfo.InvariantCulture)} "
                + (balance.Matches ? "OK" : "MISMATCH"));
        }

        foreach (var error in report.Errors) Console.Error.WriteLine($"error [{error.Key}] {error.Error}");

        Console.Error.WriteLine(
            $"holds-abort mode={report.Mode} scanned={report.RecordsScanned} actionable={report.Plans.Count} "
            + $"released={report.ReleasedTotal.ToString(CultureInfo.InvariantCulture)} "
            + $"errors={report.Errors.Count} report={path}");
    }
}

public sealed class Options
{
    public const string DefaultWalletUrl = "http://127.0.0.1:10014";
    public const string DefaultStateUrl = "http://127.0.0.1:10073";

    public required string WalletUrl { get; init; }
    public required string StateUrl { get; init; }
    public string? WalletToken { get; init; }
    public string? StateToken { get; init; }
    public string? Jeeber { get; init; }
    public bool Execute { get; init; }

    public string Mode => Execute ? "execute" : "dry-run";

    public bool Matches(string jeeberId) =>
        Jeeber is null || string.Equals(Normalize(Jeeber), Normalize(jeeberId), StringComparison.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var walletUrl = Env("WALLET_SERVICE_URL") ?? DefaultWalletUrl;
        var stateUrl = Env("JEEB_STATE_SERVICE_URL") ?? DefaultStateUrl;
        var walletToken = Env("WALLET_SERVICE_TOKEN");
        var stateToken = Env("JEEB_STATE_SERVICE_TOKEN");
        var stateTokenFile = Env("JEEB_STATE_SERVICE_TOKEN_FILE");
        string? jeeber = null;
        var execute = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wallet-url": walletUrl = Value(args, ++i); break;
                case "--state-url": stateUrl = Value(args, ++i); break;
                case "--wallet-token": walletToken = Value(args, ++i); break;
                case "--state-token": stateToken = Value(args, ++i); break;
                case "--state-token-file": stateTokenFile = Value(args, ++i); break;
                case "--jeeber": jeeber = Value(args, ++i); break;
                case "--execute": execute = true; break;
                case "--dry-run": execute = false; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(stateTokenFile))
        {
            if (!File.Exists(stateTokenFile))
                throw new ArgumentException($"state token file '{stateTokenFile}' does not exist");
            stateToken = File.ReadAllText(stateTokenFile).Trim();
        }
        if (jeeber is not null && string.IsNullOrWhiteSpace(jeeber))
            throw new ArgumentException("--jeeber needs a jeeber id");

        return new Options
        {
            WalletUrl = RequireHttp(walletUrl, "--wallet-url"),
            StateUrl = RequireHttp(stateUrl, "--state-url"),
            WalletToken = Blank(walletToken),
            StateToken = Blank(stateToken),
            Jeeber = jeeber,
            Execute = execute,
        };
    }

    public static void PrintUsage() => Console.Error.WriteLine(
        $"""
        Usage: HoldsAbort
          [--wallet-url <URL>]        (default {DefaultWalletUrl}, env WALLET_SERVICE_URL)
          [--state-url <URL>]         (default {DefaultStateUrl}, env JEEB_STATE_SERVICE_URL)
          [--wallet-token <TOKEN>]    (env WALLET_SERVICE_TOKEN; wallet-service has no auth today)
          [--state-token <TOKEN>]     (env JEEB_STATE_SERVICE_TOKEN)
          [--state-token-file <PATH>] (env JEEB_STATE_SERVICE_TOKEN_FILE; the gateway's mounted secret)
          [--jeeber <ID>]             partial mode: only this jeeber's holds
          [--dry-run]                 DEFAULT
          [--execute]

        Break-glass total release of gateway offer holds (R4). Wallet + state HTTP APIs only —
        this tool never opens a database and never writes SQL.

        Dry-run reads only: GET {Program.IntentKeyPrefix}* intent records from state-service, then
        GET Transaction/by-external-reference/{Program.ExternalReferencePrefix}<offerId> per record,
        and prints the full plan. --execute aborts every PENDING txId (idempotent), re-reads to
        confirm none is left, verifies each holder's netted balance rose by exactly what was
        released, and only then tombstones the record. A 500 "already executed" is SKIPPED and
        reported, never retried blind.

        Both modes write holdsabort-<mode>-<yyyyMMdd-HHmmss>.json to the working directory.
        Exit codes: 0 clean, 1 errors, 2 usage.
        """);

    private static string RequireHttp(string url, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? url
            : throw new ArgumentException($"{name} must be an absolute HTTP(S) URL");

    private static string Value(string[] args, int index) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException("missing value for the preceding argument");

    private static string? Env(string name) => Blank(Environment.GetEnvironmentVariable(name));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Normalize(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim();
}

/// <summary>One pooled handler for both upstreams; a console tool has no DI container.</summary>
public sealed class HttpClientPool : IDisposable
{
    private readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    public HttpClient Create(string baseUrl, string? bearerToken)
    {
        var client = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    public void Dispose() => _handler.Dispose();
}

/// <summary>The jeeb-state-service slice this tool needs: the intent-record prefix scan and the
/// tombstone overwrite. There is no DELETE on the KV, so "delete the record" IS the tombstone.</summary>
public sealed class StateApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public StateApi(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<IdempotencyRecordWire>> ListByPrefixAsync(
        string prefix, CancellationToken ct)
    {
        var url = $"v1/state/idempotency/by-prefix?prefix={Uri.EscapeDataString(prefix)}";
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, url, ct);
        return await response.Content.ReadFromJsonAsync<List<IdempotencyRecordWire>>(Json, ct)
            ?? new List<IdempotencyRecordWire>();
    }

    /// <summary>Overwrites the record with State=closed on a short TTL. The sweeper and this tool
    /// both treat a closed record as absent, so the enumeration surface stays exact.</summary>
    public async Task TombstoneAsync(HoldIntent intent, CancellationToken ct)
    {
        var body = new IdempotencyPutWire(
            Key: intent.Key,
            ResponseBody: intent.ToClosedBody(),
            StatusCode: 200,
            TtlSeconds: HoldIntent.TombstoneTtlSeconds);

        using var response = await _http.PutAsJsonAsync("v1/state/idempotency", body, Json, ct);
        await EnsureSuccessAsync(response, "v1/state/idempotency", ct);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"state-service {url} answered HTTP {(int)response.StatusCode}: "
            + (text.Length <= 300 ? text : text[..300]));
    }
}

/// <summary>The wallet-service slice this tool needs. Hand-rolled rather than reusing the gateway's
/// client because that one collapses the "already executed" 500 this tool must never retry.</summary>
public sealed class WalletApi
{
    /// <summary>wallet-service TransactionStatus: Executed=0, Pending=-1, Aborted=-2.</summary>
    public const int PendingStatus = -1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public WalletApi(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<HoldHeader>> ListByExternalReferenceAsync(
        string externalReference, CancellationToken ct)
    {
        var url = $"Transaction/by-external-reference/{Uri.EscapeDataString(externalReference)}";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<HoldHeader>();
        await EnsureSuccessAsync(response, url, ct);

        var found = await response.Content.ReadFromJsonAsync<List<TransactionWire>>(Json, ct)
            ?? new List<TransactionWire>();

        return found
            .Where(transaction => transaction.TransactionHeader is not null)
            .Select(transaction => new HoldHeader(
                transaction.TransactionHeader!.TxId,
                transaction.TransactionHeader.Status,
                (transaction.TransactionDetails ?? new List<LegWire>()).Sum(leg => leg.Amount)))
            .ToList();
    }

    public async Task<AbortOutcome> AbortAsync(Guid txId, CancellationToken ct)
    {
        var url = $"Transaction/{txId:D}/abort";
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(url, content: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AbortOutcome(AbortKind.Failed, $"transport fault: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return new AbortOutcome(AbortKind.Aborted, null);

            var text = await response.Content.ReadAsStringAsync(ct);
            // Abort-after-execute is terminal upstream ("Transaction already executed"): the money
            // has moved and no retry can take it back, so this is reported, not repeated.
            if (text.Contains("already executed", StringComparison.OrdinalIgnoreCase))
                return new AbortOutcome(AbortKind.AlreadyExecuted, Truncate(text));

            return new AbortOutcome(
                AbortKind.Failed, $"HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }
    }

    /// <summary>walletId → NETTED amount: wallet-service subtracts pending outgoing legs from this
    /// read (S-10), which is exactly why releasing a hold makes the number rise.</summary>
    public async Task<Dictionary<Guid, decimal>> ReadNettedWalletsAsync(Guid holderId, CancellationToken ct)
    {
        var url = $"Wallet/holder/{holderId:D}/wallets";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new Dictionary<Guid, decimal>();
        await EnsureSuccessAsync(response, url, ct);

        var holder = await response.Content.ReadFromJsonAsync<HolderWalletsWire>(Json, ct);
        return (holder?.Wallets ?? new List<WalletWire>())
            .Where(row => row.WalletId != Guid.Empty)
            .GroupBy(row => row.WalletId)
            .ToDictionary(group => group.Key, group => group.First().Amount);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"wallet-service {url} answered HTTP {(int)response.StatusCode}: {Truncate(text)}");
    }

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300];
}

public enum AbortKind
{
    Aborted,
    AlreadyExecuted,
    Failed,
}

public sealed record AbortOutcome(AbortKind Kind, string? Detail);

public sealed record HoldHeader(Guid TxId, int Status, decimal Amount)
{
    public bool IsPending => Status == WalletApi.PendingStatus;

    public string StatusName => Status switch
    {
        0 => "executed",
        WalletApi.PendingStatus => "pending",
        -2 => "aborted",
        _ => $"unknown({Status.ToString(CultureInfo.InvariantCulture)})",
    };
}

/// <summary>The gateway's durable intent record, read back off the state-service KV.</summary>
public sealed class HoldIntent
{
    public const int TombstoneTtlSeconds = 60;
    public const string ClosedState = "closed";

    public required string Key { get; init; }
    public required string OfferId { get; init; }
    public required string JeeberId { get; init; }
    public required string RequestId { get; init; }
    public required int Seq { get; init; }
    public required decimal ExpectedAmount { get; init; }
    public required DateTimeOffset? PlacedAtUtc { get; init; }
    public required string State { get; init; }

    public bool IsClosed => string.Equals(State, ClosedState, StringComparison.OrdinalIgnoreCase);

    public string ExternalReference => Program.ExternalReferencePrefix + OfferId;

    public static HoldIntent Parse(IdempotencyRecordWire record)
    {
        var key = record.Key ?? throw new InvalidOperationException("record carries no key");
        var body = Unwrap(record.ResponseBody);

        // The key is authoritative for the offer id: it is what the external reference is built
        // from, and a body that disagrees with its own key cannot be trusted to name the hold.
        var offerId = key.StartsWith(Program.IntentKeyPrefix, StringComparison.Ordinal)
            ? key[Program.IntentKeyPrefix.Length..]
            : throw new InvalidOperationException($"key '{key}' is outside '{Program.IntentKeyPrefix}*'");
        if (string.IsNullOrWhiteSpace(offerId))
            throw new InvalidOperationException($"key '{key}' carries no offer id");

        return new HoldIntent
        {
            Key = key,
            OfferId = offerId,
            JeeberId = Text(body, "jeeberId") ?? string.Empty,
            RequestId = Text(body, "requestId") ?? string.Empty,
            Seq = (int)(Number(body, "seq") ?? 0m),
            ExpectedAmount = Number(body, "expectedAmount") ?? 0m,
            PlacedAtUtc = Timestamp(body, "placedAtUtc"),
            State = Text(body, "state") ?? "open",
        };
    }

    public Dictionary<string, object?> ToClosedBody() => new()
    {
        ["offerId"] = OfferId,
        ["jeeberId"] = JeeberId,
        ["requestId"] = RequestId,
        ["seq"] = Seq,
        ["expectedAmount"] = ExpectedAmount,
        ["placedAtUtc"] = PlacedAtUtc,
        ["state"] = ClosedState,
        ["closedBy"] = "tools/HoldsAbort",
        ["closedAtUtc"] = DateTimeOffset.UtcNow,
    };

    /// <summary>Some writers store the record as an object, others as a JSON string; both are read
    /// here rather than assuming one shape and silently reporting every record unreadable.</summary>
    private static JsonElement Unwrap(JsonElement? responseBody)
    {
        if (responseBody is not { } body)
            throw new InvalidOperationException("record carries no responseBody");
        if (body.ValueKind != JsonValueKind.String) return body;

        using var parsed = JsonDocument.Parse(body.GetString() ?? "{}");
        return parsed.RootElement.Clone();
    }

    private static JsonElement? Property(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static string? Text(JsonElement body, string name) =>
        Property(body, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static decimal? Number(JsonElement body, string name)
    {
        if (Property(body, name) is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDecimal();
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? Timestamp(JsonElement body, string name)
    {
        if (Property(body, name) is not { ValueKind: JsonValueKind.String } value) return null;
        return DateTimeOffset.TryParse(
            value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}

// ── wire shapes (state-service + wallet-service DTOs) ──

public sealed class IdempotencyRecordWire
{
    public string? Key { get; set; }
    public JsonElement? ResponseBody { get; set; }
    public int? StatusCode { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record IdempotencyPutWire(
    string Key, object? ResponseBody, int StatusCode, int TtlSeconds);

public sealed class TransactionWire
{
    public TransactionHeaderWire? TransactionHeader { get; set; }
    public List<LegWire>? TransactionDetails { get; set; }
}

public sealed class TransactionHeaderWire
{
    public Guid TxId { get; set; }
    public string? ServiceName { get; set; }
    public string? Tag { get; set; }
    public string? ExternalReference { get; set; }
    public int Status { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class LegWire
{
    public Guid SourceWalletId { get; set; }
    public Guid DestinationWalletId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class HolderWalletsWire
{
    public List<WalletWire>? Wallets { get; set; }
}

public sealed class WalletWire
{
    public Guid WalletId { get; set; }

    [JsonPropertyName("currencyID")]
    public int CurrencyId { get; set; }

    public decimal Amount { get; set; }
    public string? Type { get; set; }
    public bool IsActive { get; set; }
}

// ── report ──

public sealed class AbortReport
{
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("wallet_url")]
    public string WalletUrl { get; set; } = string.Empty;

    [JsonPropertyName("state_url")]
    public string StateUrl { get; set; } = string.Empty;

    [JsonPropertyName("jeeber_filter")]
    public string? JeeberFilter { get; set; }

    [JsonPropertyName("records_scanned")]
    public int RecordsScanned { get; set; }

    [JsonPropertyName("released_total")]
    public decimal ReleasedTotal { get; set; }

    [JsonPropertyName("plans")]
    public List<HoldPlan> Plans { get; } = new();

    [JsonPropertyName("already_tombstoned")]
    public List<string> AlreadyTombstoned { get; } = new();

    [JsonPropertyName("filtered_out")]
    public List<string> FilteredOut { get; } = new();

    [JsonPropertyName("balances")]
    public List<BalanceVerification> Balances { get; } = new();

    [JsonPropertyName("errors")]
    public List<ItemError> Errors { get; } = new();
}

public sealed class HoldPlan
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("offer_id")]
    public string OfferId { get; set; } = string.Empty;

    [JsonPropertyName("jeeber_id")]
    public string JeeberId { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("expected_amount")]
    public decimal ExpectedAmount { get; set; }

    [JsonPropertyName("placed_at_utc")]
    public DateTimeOffset? PlacedAtUtc { get; set; }

    [JsonPropertyName("external_reference")]
    public string ExternalReference { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public IReadOnlyList<HoldHeaderView> Headers { get; set; } = Array.Empty<HoldHeaderView>();

    [JsonPropertyName("pending_total")]
    public decimal PendingTotal { get; set; }

    [JsonPropertyName("aborted_tx_ids")]
    public List<string> AbortedTxIds { get; } = new();

    [JsonPropertyName("already_executed_tx_ids")]
    public List<string> AlreadyExecutedTxIds { get; } = new();

    [JsonPropertyName("aborted_total")]
    public decimal AbortedTotal { get; set; }

    [JsonPropertyName("tombstoned")]
    public bool Tombstoned { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "pending";
}

public sealed record HoldHeaderView(
    [property: JsonPropertyName("tx_id")] string TxId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("amount")] decimal Amount);

public sealed record BalanceVerification(
    [property: JsonPropertyName("holder_id")] string HolderId,
    [property: JsonPropertyName("expected")] decimal Expected,
    [property: JsonPropertyName("released")] decimal Released,
    [property: JsonPropertyName("matches")] bool Matches);

public sealed record ItemError(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("error")] string Error);
