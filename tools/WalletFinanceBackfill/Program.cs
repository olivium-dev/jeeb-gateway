using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Financials;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace WalletFinanceBackfill;

/// <summary>
/// Owner-run gateway/delivery -> wallet reconciliation and backfill tool. It is not wired into
/// startup or deployment and defaults to dry-run: no holder provisioning and no transaction POST.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(console => console.SingleLine = true));
        using var walletHttp = new HttpClient
        {
            BaseAddress = new Uri(options.WalletBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var factory = new FixedHttpClientFactory(walletHttp);
        var wallet = new WalletSettlementLedgerClient(
            factory, loggerFactory.CreateLogger<WalletSettlementLedgerClient>());
        var provisioner = new WalletProvisioner(walletHttp);
        var source = new NpgsqlBackfillSource(options);
        var runner = new BackfillRunner(options, wallet, provisioner, source);

        BackfillSummary summary;
        try
        {
            summary = await runner.RunAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("WalletFinanceBackfill")
                .LogError(ex, "Backfill aborted before reconciliation completed.");
            return 1;
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(summary, Json));
        if (summary.Errors > 0) return 1;
        if (options.RequireClean && summary.ReconciliationMismatches > 0) return 3;
        return 0;
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Usage: WalletFinanceBackfill
          --user-management-dsn-env <ENV_VAR_NAME>
          --gateway-dsn-env <ENV_VAR_NAME>
          --delivery-dsn-env <ENV_VAR_NAME>
          --wallet-base-url <PRIVATE_OVERLAY_URL>
          [--require-clean] [--dry-run]
          [--execute --confirm wallet-authoritative-backfill]

        Default mode is dry-run: all three databases and wallet inventory are read, but there are
        zero wallet PUT/POST requests. Execute mode first idempotently ensures configured wallets
        for every active user-management driver, then posts each matching financial settlement
        under wallet key settlement:<gateway-settlement-id>.
        Connection strings are read only from the named environment variables and never logged.
        """);
}

public sealed class Options
{
    private const string Confirmation = "wallet-authoritative-backfill";

    public required string UserManagementDsn { get; init; }
    public required string GatewayDsn { get; init; }
    public required string DeliveryDsn { get; init; }
    public required string WalletBaseUrl { get; init; }
    public bool Execute { get; init; }
    public bool RequireClean { get; init; }

    public static Options Parse(string[] args)
    {
        string? userManagementEnv = null;
        string? gatewayEnv = null;
        string? deliveryEnv = null;
        string? walletBaseUrl = null;
        string? confirmation = null;
        var execute = false;
        var requireClean = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--user-management-dsn-env": userManagementEnv = Value(args, ++i); break;
                case "--gateway-dsn-env": gatewayEnv = Value(args, ++i); break;
                case "--delivery-dsn-env": deliveryEnv = Value(args, ++i); break;
                case "--wallet-base-url": walletBaseUrl = Value(args, ++i); break;
                case "--execute": execute = true; break;
                case "--dry-run": execute = false; break;
                case "--confirm": confirmation = Value(args, ++i); break;
                case "--require-clean": requireClean = true; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (string.IsNullOrWhiteSpace(userManagementEnv))
            throw new ArgumentException("--user-management-dsn-env is required");
        if (string.IsNullOrWhiteSpace(gatewayEnv))
            throw new ArgumentException("--gateway-dsn-env is required");
        if (string.IsNullOrWhiteSpace(deliveryEnv))
            throw new ArgumentException("--delivery-dsn-env is required");
        if (string.IsNullOrWhiteSpace(walletBaseUrl)
            || !Uri.TryCreate(walletBaseUrl, UriKind.Absolute, out var walletUri)
            || walletUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--wallet-base-url must be an absolute HTTP(S) URL");
        }
        if (execute && !string.Equals(confirmation, Confirmation, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"--execute requires '--confirm {Confirmation}'");
        }

        return new Options
        {
            UserManagementDsn = ReadSecret(userManagementEnv),
            GatewayDsn = ReadSecret(gatewayEnv),
            DeliveryDsn = ReadSecret(deliveryEnv),
            WalletBaseUrl = walletBaseUrl,
            Execute = execute,
            RequireClean = requireClean,
        };
    }

    private static string ReadSecret(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"environment variable '{variable}' is unset or empty")
            : value;
    }

    private static string Value(string[] args, int index) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException("missing value for the preceding argument");
}

public sealed record GatewaySettlement(
    string Id,
    string DeliveryId,
    string JeeberId,
    string ClientId,
    decimal GoodsCost,
    decimal Commission,
    decimal Insurance,
    decimal Total,
    string Currency,
    string PaymentMethod,
    DateTimeOffset SettledAt,
    string? LegacyLedgerEntryId);

public sealed record DeliveryMarker(
    string DeliveryId,
    string JeeberId,
    string ClientId,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record BackfillRow(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("settlement_id")] string? SettlementId,
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("jeeber_id")] string JeeberId,
    [property: JsonPropertyName("source_coverage")] string SourceCoverage,
    [property: JsonPropertyName("identity_match")] bool? IdentityMatch,
    [property: JsonPropertyName("wallet_inventory")] string WalletInventory,
    [property: JsonPropertyName("wallet_transaction_id")] string? WalletTransactionId,
    string Mode,
    string Outcome,
    string? Error);

public sealed record HolderBackfillRow(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("holder_id")] string HolderId,
    [property: JsonPropertyName("identity_source")] string IdentitySource,
    [property: JsonPropertyName("wallet_inventory")] string WalletInventory,
    string Mode,
    string Outcome,
    string? Error);

public sealed class BackfillSummary
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("gateway_settlements")]
    public int GatewaySettlements { get; set; }

    [JsonPropertyName("delivery_markers")]
    public int DeliveryMarkers { get; set; }

    [JsonPropertyName("authoritative_jeeber_holders")]
    public int AuthoritativeJeeberHolders { get; set; }

    [JsonPropertyName("wallet_holders_ensured")]
    public int WalletHoldersEnsured { get; set; }

    [JsonPropertyName("wallet_posts_succeeded")]
    public int WalletPostsSucceeded { get; set; }

    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    [JsonPropertyName("reconciliation_mismatches")]
    public int ReconciliationMismatches { get; set; }
}

public interface IBackfillSource
{
    Task<IReadOnlyList<Guid>> ReadAuthoritativeJeeberIdsAsync(CancellationToken ct);
    Task<IReadOnlyList<GatewaySettlement>> ReadGatewaySettlementsAsync(CancellationToken ct);
    Task<IReadOnlyList<DeliveryMarker>> ReadDeliveryMarkersAsync(CancellationToken ct);
}

public interface IWalletProvisioner
{
    Task<IReadOnlyList<WalletCurrency>> ReadCurrenciesAsync(CancellationToken ct);

    Task<WalletInspection> InspectAsync(
        Guid holderId,
        IReadOnlyList<WalletCurrency> currencies,
        CancellationToken ct);

    Task EnsureAsync(
        Guid holderId,
        IReadOnlyList<WalletCurrency> currencies,
        WalletInspection inspection,
        CancellationToken ct);
}

public sealed class BackfillRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Options _options;
    private readonly ISettlementLedgerClient _wallet;
    private readonly IWalletProvisioner _provisioner;
    private readonly IBackfillSource _source;
    private readonly Action<object> _write;

    public BackfillRunner(
        Options options,
        ISettlementLedgerClient wallet,
        IWalletProvisioner provisioner,
        IBackfillSource source,
        Action<object>? write = null)
    {
        _options = options;
        _wallet = wallet;
        _provisioner = provisioner;
        _source = source;
        _write = write ?? Write;
    }

    public async Task<BackfillSummary> RunAsync(CancellationToken ct)
    {
        var authoritativeIds = (await _source.ReadAuthoritativeJeeberIdsAsync(ct))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var authoritativeSet = authoritativeIds.ToHashSet();
        var gatewayRows = await _source.ReadGatewaySettlementsAsync(ct);
        var deliveryRows = await _source.ReadDeliveryMarkersAsync(ct);
        var deliveryById = deliveryRows
            .GroupBy(row => row.DeliveryId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.CreatedAt).First(),
                StringComparer.Ordinal);
        var gatewayDeliveryIds = gatewayRows.Select(row => row.DeliveryId)
            .ToHashSet(StringComparer.Ordinal);
        var currencies = await _provisioner.ReadCurrenciesAsync(ct);
        if (currencies.Count == 0)
            throw new InvalidOperationException("Wallet-service exposes no configured currencies.");

        var summary = new BackfillSummary
        {
            Mode = _options.Execute ? "execute" : "dry-run",
            GatewaySettlements = gatewayRows.Count,
            DeliveryMarkers = deliveryRows.Count,
            AuthoritativeJeeberHolders = authoritativeIds.Length,
        };

        var holderReadiness = new Dictionary<Guid, HolderReadiness>();
        foreach (var holderId in authoritativeIds)
        {
            ct.ThrowIfCancellationRequested();
            var inventory = "unchecked";
            string outcome;
            string? error = null;
            try
            {
                var inspection = await _provisioner.InspectAsync(holderId, currencies, ct);
                inventory = inspection.State;
                if (_options.Execute)
                {
                    await _provisioner.EnsureAsync(holderId, currencies, inspection, ct);
                    inventory = "ready";
                    outcome = "ensured_or_replayed";
                    summary.WalletHoldersEnsured++;
                    holderReadiness[holderId] = new HolderReadiness(true, inventory, null);
                }
                else
                {
                    outcome = inspection.Ready ? "ready" : "provisioning_required";
                    if (!inspection.Ready) summary.ReconciliationMismatches++;
                    holderReadiness[holderId] = new HolderReadiness(
                        inspection.Ready, inventory, inspection.Ready ? null : outcome);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Errors++;
                outcome = "error";
                error = ex.Message;
                holderReadiness[holderId] = new HolderReadiness(false, inventory, error);
            }

            _write(new HolderBackfillRow(
                "authoritative_jeeber_wallet",
                holderId.ToString("D"),
                "user_management_available_roles_driver",
                inventory,
                summary.Mode,
                outcome,
                error));
        }

        foreach (var row in gatewayRows)
        {
            ct.ThrowIfCancellationRequested();
            deliveryById.TryGetValue(row.DeliveryId, out var marker);
            bool? identityMatch = marker is null
                ? null
                : string.Equals(marker.JeeberId, row.JeeberId, StringComparison.Ordinal)
                  && string.Equals(marker.ClientId, row.ClientId, StringComparison.Ordinal);
            if (marker is null || identityMatch == false) summary.ReconciliationMismatches++;

            var inventory = "unchecked";
            string? transactionId = null;
            string outcome;
            string? error = null;
            try
            {
                if (!Guid.TryParse(row.JeeberId, out var holderId) || holderId == Guid.Empty)
                    throw new InvalidOperationException("Jeeber id is not a non-system wallet holder UUID.");
                if (!authoritativeSet.Contains(holderId))
                {
                    summary.ReconciliationMismatches++;
                    throw new InvalidOperationException(
                        "Settlement Jeeber is absent from the active user-management driver population; "
                        + "no holder is inferred and no wallet write is attempted.");
                }

                var readiness = holderReadiness[holderId];
                inventory = readiness.Inventory;
                if (!readiness.Ready)
                {
                    throw new InvalidOperationException(
                        "Authoritative Jeeber wallet inventory is not ready; settlement was not posted."
                        + (readiness.Error is null ? string.Empty : $" {readiness.Error}"));
                }

                if (_options.Execute)
                {
                    var posted = await _wallet.PostLedgerEntryAsync(new LedgerEntryRequest
                    {
                        DeliveryId = row.DeliveryId,
                        JeeberId = row.JeeberId,
                        ClientId = row.ClientId,
                        EntryType = "cash_settlement",
                        GoodsCost = row.GoodsCost,
                        Commission = row.Commission,
                        Insurance = row.Insurance,
                        Total = row.Total,
                        Currency = row.Currency,
                        PaymentMethod = row.PaymentMethod,
                        IdempotencyKey = row.Id,
                    }, ct);
                    transactionId = posted.LedgerEntryId;
                    summary.WalletPostsSucceeded++;
                    outcome = "posted_or_replayed";
                }
                else
                {
                    outcome = "ready_to_backfill";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.Errors++;
                outcome = "error";
                error = ex.Message;
            }

            _write(new BackfillRow(
                "gateway_financial_settlement",
                row.Id,
                row.DeliveryId,
                row.JeeberId,
                marker is null ? "gateway_only" : "gateway_and_delivery",
                identityMatch,
                inventory,
                transactionId,
                summary.Mode,
                outcome,
                error));
        }

        foreach (var marker in deliveryById.Values.Where(
                     row => !gatewayDeliveryIds.Contains(row.DeliveryId)))
        {
            summary.ReconciliationMismatches++;
            _write(new BackfillRow(
                "delivery_marker_without_financial_source",
                null,
                marker.DeliveryId,
                marker.JeeberId,
                "delivery_only",
                null,
                "not_applicable",
                null,
                summary.Mode,
                "flagged_no_amounts_to_backfill",
                "Delivery marker has no gateway financial settlement; no amount is inferred or written."));
        }

        return summary;
    }

    private static void Write(object row) =>
        Console.WriteLine(JsonSerializer.Serialize(row, row.GetType(), Json));

    private sealed record HolderReadiness(bool Ready, string Inventory, string? Error);
}

/// <summary>
/// Read-only source adapter. User Management is the only population authority: settlement rows
/// are reconciliation inputs, never an implicit instruction to create a holder.
/// </summary>
public sealed class NpgsqlBackfillSource : IBackfillSource
{
    public const string AuthoritativeJeeberSql = """
        SELECT "Id"
        FROM "Users"
        WHERE "Email" <> 'Removed'
          AND "Username" <> 'Removed'
          AND "AvailableRoles" @> ARRAY['driver']::text[]
          AND "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
        ORDER BY "Id"
        """;

    private readonly Options _options;

    public NpgsqlBackfillSource(Options options) => _options = options;

    public async Task<IReadOnlyList<Guid>> ReadAuthoritativeJeeberIdsAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_options.UserManagementDsn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(AuthoritativeJeeberSql, conn);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    public async Task<IReadOnlyList<GatewaySettlement>> ReadGatewaySettlementsAsync(
        CancellationToken ct)
    {
        const string sql = """
            SELECT id::text, delivery_id, jeeber_id, client_id,
                   goods_cost, commission, insurance, total,
                   currency, payment_method, settled_at, ledger_entry_id
            FROM settlements
            WHERE state IN ('settled', 'receipt_generated')
            ORDER BY settled_at, id
            """;
        await using var conn = new NpgsqlConnection(_options.GatewayDsn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var rows = new List<GatewaySettlement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new GatewaySettlement(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7),
                reader.GetString(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<DeliveryMarker>> ReadDeliveryMarkersAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_options.DeliveryDsn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT s.delivery_id::text, COALESCE(d.jeeber_id, ''), d.client_id,
                   d.status, s.created_at
            FROM settlements s
            JOIN deliveries d ON d.id = s.delivery_id
            ORDER BY s.created_at, s.delivery_id
            """, conn);
        var rows = new List<DeliveryMarker>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DeliveryMarker(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return rows;
    }
}

public sealed class WalletProvisioner : IWalletProvisioner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string DefaultWalletType = "jeeb";
    private readonly HttpClient _http;

    public WalletProvisioner(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<WalletCurrency>> ReadCurrenciesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            "Fees/currencies", HttpCompletionOption.ResponseHeadersRead, ct);
        await Success(response, "read wallet currencies", ct);
        var currencies = await response.Content.ReadFromJsonAsync<List<WalletCurrency>>(Json, ct)
            ?? new List<WalletCurrency>();
        if (currencies.Count == 0
            || currencies.Any(currency => currency.Id <= 0)
            || currencies.Any(currency => string.IsNullOrWhiteSpace(currency.Code))
            || currencies.GroupBy(currency => currency.Id).Any(group => group.Count() != 1)
            || currencies.GroupBy(currency => currency.Code, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException(
                "Wallet-service must expose a non-empty, unambiguous configured currency set.");
        }

        return currencies;
    }

    public async Task<WalletInspection> InspectAsync(
        Guid holderId,
        IReadOnlyList<WalletCurrency> currencies,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"Wallet/holder/{holderId:D}/wallets", HttpCompletionOption.ResponseHeadersRead, ct);
        await Success(response, "read holder wallets", ct);
        var holder = await response.Content.ReadFromJsonAsync<WalletHolderResponse>(Json, ct)
            ?? new WalletHolderResponse();
        var active = (holder.Wallets ?? Array.Empty<WalletAccount>())
            .Where(wallet => wallet.IsActive)
            .ToArray();
        var ambiguous = currencies.Any(currency =>
            active.Count(wallet => wallet.CurrencyId == currency.Id) > 1);
        if (ambiguous)
            return new WalletInspection(holder, false, "ambiguous_duplicate_active_currency_wallets");
        var ready = holder.WalletHolder is not null
            && holder.WalletHolder.HolderId == holderId
            && holder.WalletHolder.IsActive
            && currencies.All(currency =>
            {
                var matches = active.Where(wallet => wallet.CurrencyId == currency.Id).ToArray();
                return matches.Length == 1 && matches[0].WalletId != Guid.Empty;
            });
        return new WalletInspection(
            holder,
            ready,
            holder.WalletHolder is null
                ? "holder_missing"
                : holder.WalletHolder.HolderId != holderId || !holder.WalletHolder.IsActive
                    ? "holder_inactive_or_mismatched"
                : ready ? "ready" : "configured_wallets_missing");
    }

    public async Task EnsureAsync(
        Guid holderId,
        IReadOnlyList<WalletCurrency> currencies,
        WalletInspection inspection,
        CancellationToken ct)
    {
        if (inspection.State == "ambiguous_duplicate_active_currency_wallets")
            throw new InvalidOperationException(
                "Holder has more than one active wallet for a configured currency; reconcile manually.");
        if (inspection.Ready) return;

        var existing = inspection.Response.Wallets ?? Array.Empty<WalletAccount>();
        var missing = currencies
            .Where(currency => existing.Count(wallet =>
                wallet.IsActive && wallet.CurrencyId == currency.Id) == 0)
            .Select(currency => new EnsureWallet(currency.Id, DefaultWalletType, "wallet-authority-backfill"))
            .ToArray();
        if (missing.Length == 0)
        {
            throw new InvalidOperationException(
                $"Holder inventory is not ready and cannot be repaired additively: {inspection.State}.");
        }

        var currentHolder = inspection.Response.WalletHolder;
        if (currentHolder is not null && currentHolder.HolderId != holderId)
            throw new InvalidOperationException("Wallet holder id does not match the authoritative Jeeber id.");
        var payload = new EnsureHolderRequest(
            new WalletHolderPayload(
                holderId,
                currentHolder?.HolderName ?? holderId.ToString("D"),
                currentHolder?.HolderType ?? "jeeber"),
            missing);
        using var response = await _http.PutAsJsonAsync("Wallet/holder/ensure", payload, Json, ct);
        await Success(response, "ensure holder wallets", ct);

        var verified = await InspectAsync(holderId, currencies, ct);
        if (!verified.Ready)
            throw new InvalidOperationException(
                $"Holder wallet provisioning did not converge: {verified.State}.");
    }

    private static async Task Success(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        _ = await response.Content.ReadAsByteArrayAsync(ct);
        throw new InvalidOperationException(
            $"Wallet-service could not {operation} (status {(int)response.StatusCode}).");
    }
}

public sealed record WalletInspection(
    WalletHolderResponse Response,
    bool Ready,
    string State);

public sealed class WalletCurrency
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;
}

public sealed class WalletHolderResponse
{
    public WalletHolder? WalletHolder { get; set; }
    public IReadOnlyList<WalletAccount>? Wallets { get; set; }
}

public sealed class WalletHolder
{
    public Guid HolderId { get; set; }
    public string HolderName { get; set; } = string.Empty;
    public string HolderType { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class WalletAccount
{
    public Guid WalletId { get; set; }

    [JsonPropertyName("currencyID")]
    public int CurrencyId { get; set; }

    public bool IsActive { get; set; }
}

public sealed record EnsureHolderRequest(
    WalletHolderPayload WalletHolder,
    IReadOnlyList<EnsureWallet> Wallets);

public sealed record WalletHolderPayload(
    Guid HolderId,
    string HolderName,
    string HolderType);

public sealed record EnsureWallet(int CurrencyId, string Type, string Note);

public sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (!string.Equals(name, WalletSettlementLedgerClient.HttpClientName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected HttpClient name '{name}'.");
        return client;
    }
}
