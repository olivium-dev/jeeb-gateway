using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.JeebWallet;
using Npgsql;

namespace WalletHolderBackfill;

/// <summary>OD-C2-1 — idempotent wallet-holder backfill for users the provisioning seam never covered.
/// Dry-run by default; every wallet WRITE goes through the gateway's own provisioner over HTTP.</summary>
public static class Program
{
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
            PrintUsage();
            return 2;
        }

        var ct = CancellationToken.None;
        IReadOnlyList<Guid> users;
        try
        {
            users = await UserSource.ReadAsync(options, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not enumerate users: {ex.Message}");
            return 1;
        }

        using var factory = new WalletHttpClientFactory(
            new Uri(options.WalletUrl.TrimEnd('/') + "/"));
        var wallet = new WalletReadApi(
            factory.CreateClient(WalletServiceJeeberWalletProvisioner.HttpClientName));
        var provisioner = new WalletServiceJeeberWalletProvisioner(factory);

        var report = new CensusReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Mode = options.Mode,
            WalletUrl = options.WalletUrl,
            UsersScanned = users.Count,
        };

        IReadOnlyList<CurrencyRow> currencies;
        try
        {
            currencies = await wallet.ReadCurrenciesAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not read the wallet currency catalog: {ex.Message}");
            return 1;
        }

        report.CurrencyIds = currencies.Select(currency => currency.Id).OrderBy(id => id).ToList();

        foreach (var userId in users)
        {
            try
            {
                var holder = await wallet.ReadHolderAsync(userId, ct);
                var missing = MissingCurrencyIds(currencies, holder);
                if (missing.Count == 0)
                {
                    report.Complete.Add(userId.ToString("D"));
                    continue;
                }

                report.Incomplete.Add(new IncompleteUser(
                    userId.ToString("D"), holder.WalletHolder is not null, missing));
                if (!options.Apply) continue;

                // Only ids enumerated from the Users source ever reach EnsureAsync: wallet-service
                // has no auth and would mint a holder for any GUID it is handed (census §3).
                await provisioner.EnsureAsync(userId, ct);
                report.Created.Add(userId.ToString("D"));
            }
            catch (Exception ex)
            {
                report.Errors.Add(new UserError(userId.ToString("D"), ex.Message));
            }
        }

        var path = Path.Combine(
            Environment.CurrentDirectory, $"walletholder-census-{DateTime.UtcNow:yyyyMMdd}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json), ct);

        Console.Error.WriteLine(
            $"walletholder-backfill mode={report.Mode} users={report.UsersScanned} "
            + $"complete={report.Complete.Count} incomplete={report.Incomplete.Count} "
            + $"created={report.Created.Count} errors={report.Errors.Count} census={path}");
        return report.Errors.Count > 0 ? 1 : 0;
    }

    /// <summary>Configured currencies with no active spendable wallet. A holder that does not exist
    /// (literal <c>{}</c> body) or is inactive reports every currency as missing.</summary>
    private static IReadOnlyList<int> MissingCurrencyIds(
        IReadOnlyList<CurrencyRow> currencies,
        HolderWallets holder)
    {
        var holderReady = holder.WalletHolder is not null && holder.WalletHolder.IsActive;
        var active = (holder.Wallets ?? Array.Empty<WalletRow>())
            .Where(row => row.IsActive
                && row.WalletId != Guid.Empty
                && SpendableWalletTypes.IsSpendable(row.Type))
            .ToArray();

        return currencies
            .Where(currency => !holderReady
                || !active.Any(row => row.CurrencyId == currency.Id))
            .Select(currency => currency.Id)
            .ToList();
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        $"""
        Usage: WalletHolderBackfill
          [--wallet-url <WALLET_SERVICE_URL>]   (default {Options.DefaultWalletUrl})
          (--users-file <PATH> | --um-connection <NPGSQL_CONNECTION_STRING>)
          [--dry-run]                           (DEFAULT)
          [--apply]

        Exactly one user source is required. --users-file takes one GUID per line ('#' comments and
        blank lines are skipped); --um-connection runs the read-only `{UserSource.UsersQuery}`
        against user-management. Those ids are the ONLY ids that can ever be ensured.

        Dry-run reads only: GET Fees/currencies once, then GET Wallet/holder/<id>/wallets per user.
        A user is INCOMPLETE when any configured currency has no active spendable (non-cod_*) wallet.
        --apply calls the gateway's own PUT Wallet/holder/ensure per incomplete user (idempotent).
        Both modes write walletholder-census-<yyyyMMdd>.json to the working directory.
        Exit codes: 0 clean, 1 errors, 2 usage.
        """);
}

public sealed class Options
{
    public const string DefaultWalletUrl = "http://127.0.0.1:10014";

    public required string WalletUrl { get; init; }
    public string? UsersFile { get; init; }
    public string? UmConnection { get; init; }
    public bool Apply { get; init; }

    public string Mode => Apply ? "apply" : "dry-run";

    public static Options Parse(string[] args)
    {
        var walletUrl = DefaultWalletUrl;
        string? usersFile = null;
        string? umConnection = null;
        var apply = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wallet-url": walletUrl = Value(args, ++i); break;
                case "--users-file": usersFile = Value(args, ++i); break;
                case "--um-connection": umConnection = Value(args, ++i); break;
                case "--apply": apply = true; break;
                case "--dry-run": apply = false; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (!Uri.TryCreate(walletUrl, UriKind.Absolute, out var walletUri)
            || walletUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--wallet-url must be an absolute HTTP(S) URL");
        }
        if (string.IsNullOrWhiteSpace(usersFile) == string.IsNullOrWhiteSpace(umConnection))
            throw new ArgumentException("supply exactly one of --users-file or --um-connection");

        return new Options
        {
            WalletUrl = walletUrl,
            UsersFile = usersFile,
            UmConnection = umConnection,
            Apply = apply,
        };
    }

    private static string Value(string[] args, int index) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException("missing value for the preceding argument");
}

/// <summary>The user-management Users table is the ONLY id source: wallet-service has no auth, so
/// an id that is not a real user must never reach holder/ensure.</summary>
public static class UserSource
{
    public const string UsersQuery = "SELECT \"Id\" FROM \"Users\"";

    public static async Task<IReadOnlyList<Guid>> ReadAsync(Options options, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(options.UmConnection)
            ? ReadFile(options.UsersFile!)
            : await ReadUserManagementAsync(options.UmConnection!, ct);

    private static IReadOnlyList<Guid> ReadFile(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"users file '{path}' does not exist");

        var ids = new List<Guid>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!Guid.TryParse(line, out var id))
                throw new ArgumentException($"users file line '{line}' is not a GUID");
            ids.Add(id);
        }

        return Normalize(ids);
    }

    private static async Task<IReadOnlyList<Guid>> ReadUserManagementAsync(
        string connectionString,
        CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Read-only by construction: one SELECT, no transaction, no write statement in this tool.
        using var command = new NpgsqlCommand(UsersQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!await reader.IsDBNullAsync(0, ct)) ids.Add(reader.GetGuid(0));
        }

        return Normalize(ids);
    }

    private static IReadOnlyList<Guid> Normalize(IEnumerable<Guid> ids) =>
        ids.Where(id => id != Guid.Empty).Distinct().OrderBy(id => id).ToList();
}

/// <summary>Supplies the one named client the reused gateway provisioner asks for; a console tool
/// has no DI container, and every client shares a single pooled handler.</summary>
public sealed class WalletHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    private readonly Uri _baseAddress;

    public WalletHttpClientFactory(Uri baseAddress) => _baseAddress = baseAddress;

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false)
        {
            BaseAddress = _baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };

    public void Dispose() => _handler.Dispose();
}

/// <summary>The read-only slice of wallet-service this census needs. Writes are NOT here on
/// purpose: they belong to <see cref="WalletServiceJeeberWalletProvisioner"/>, reused verbatim.</summary>
public sealed class WalletReadApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public WalletReadApi(HttpClient http) => _http = http;

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

    public async Task<HolderWallets> ReadHolderAsync(Guid holderId, CancellationToken ct) =>
        await GetAsync<HolderWallets>($"Wallet/holder/{holderId:D}/wallets", ct) ?? new HolderWallets();

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"wallet-service could not read {url} (HTTP {(int)response.StatusCode}): "
                + (body.Length <= 300 ? body : body[..300]));
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}

public sealed class CurrencyRow
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;
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
    public bool IsActive { get; set; }
}

public sealed class CensusReport
{
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("wallet_url")]
    public string WalletUrl { get; set; } = string.Empty;

    [JsonPropertyName("currency_ids")]
    public IReadOnlyList<int> CurrencyIds { get; set; } = Array.Empty<int>();

    [JsonPropertyName("users_scanned")]
    public int UsersScanned { get; set; }

    [JsonPropertyName("complete")]
    public List<string> Complete { get; } = new();

    [JsonPropertyName("incomplete")]
    public List<IncompleteUser> Incomplete { get; } = new();

    [JsonPropertyName("created")]
    public List<string> Created { get; } = new();

    [JsonPropertyName("errors")]
    public List<UserError> Errors { get; } = new();
}

public sealed record IncompleteUser(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("holder_exists")] bool HolderExists,
    [property: JsonPropertyName("missing_currency_ids")] IReadOnlyList<int> MissingCurrencyIds);

public sealed record UserError(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("error")] string Error);
