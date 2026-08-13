using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.StateService.Ownership;
using JeebGateway.Users.DataExport;
using Npgsql;

namespace DataExportRelay;

/// <summary>
/// gwdbx W1-07 — relays OPEN data_exports rows (queued/processing/ready) into state-service
/// work-items. Terminal rows are archive-only. Idempotent: re-running is a no-op.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        var rows = await ReadOpenRowsAsync(opts.ConnectionString);
        DataExportRelayPlan.AssertOneOpenRowPerUser(rows);
        Console.Error.WriteLine(
            $"[read] open rows={rows.Count} statuses=[{string.Join(",", DataExportRelayPlan.RelayStatuses)}] "
            + $"mode={(opts.Execute ? "execute" : "dry-run")}");

        using var http = NewClient(opts);
        var created = 0;
        var verified = 0;
        var failed = 0;

        foreach (var row in rows)
        {
            var key = DataExportRelayPlan.IdempotencyKeyFor(row);
            Console.Error.WriteLine(
                $"[row] export={row.ExportId} subject={row.UserId} status={row.Status} "
                + $"dueBy={row.DueBy:O} key={key}");

            if (!opts.Execute)
            {
                continue;
            }

            try
            {
                await PostWorkItemAsync(http, DataExportRelayPlan.BuildWorkItem(row), key);
                created++;
                if (await VerifyAsync(http, row))
                {
                    verified++;
                }
                else
                {
                    failed++;
                    Console.Error.WriteLine($"[verify] MISS export={row.ExportId} — no matching work item");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[error] export={row.ExportId}: {ex.Message}");
            }
        }

        Console.Error.WriteLine(
            $"[summary] open={rows.Count} posted={created} verified={verified} failed={failed} "
            + $"mode={(opts.Execute ? "execute" : "dry-run")}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<IReadOnlyList<DataExportRelayPlan.RelayRow>> ReadOpenRowsAsync(string dsn)
    {
        await using var conn = new NpgsqlConnection(dsn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(DataExportRelayPlan.SelectOpenSql, conn);
        cmd.Parameters.AddWithValue(
            DataExportRelayPlan.RelayStatusesParameter, DataExportRelayPlan.RelayStatuses.ToArray());

        var rows = new List<DataExportRelayPlan.RelayRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DataExportRelayPlan.RelayRow(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return rows;
    }

    private static async Task PostWorkItemAsync(
        HttpClient http, WorkItemCreateRequestV1 body, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/work-items")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        Console.Error.WriteLine($"[post] HTTP {(int)response.StatusCode} {payload}");
        response.EnsureSuccessStatusCode();
    }

    // Reads the item back through the same API the gateway uses, so a 2xx that stored
    // nothing cannot be mistaken for a relayed row.
    private static async Task<bool> VerifyAsync(HttpClient http, DataExportRelayPlan.RelayRow row)
    {
        var path = "v1/work-items/latest?application="
            + Uri.EscapeDataString(MirroringDataExportStore.Application)
            + "&kind=" + Uri.EscapeDataString(MirroringDataExportStore.WorkKind)
            + "&subjectRef=" + Uri.EscapeDataString(row.UserId);

        using var response = await http.GetAsync(path);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();

        var item = await response.Content.ReadFromJsonAsync<WorkItemRecordV1>(Json);
        if (item is null || item.Payload.ValueKind != JsonValueKind.Object
            || !item.Payload.TryGetProperty("exportId", out var exportId))
        {
            return false;
        }
        Console.Error.WriteLine(
            $"[verify] workItemId={item.WorkItemId} status={item.Status} dueAt={item.DueAt:O}");
        return string.Equals(exportId.GetString(), row.ExportId, StringComparison.Ordinal);
    }

    private static HttpClient NewClient(Options opts)
    {
        var token = File.ReadAllText(opts.TokenFile).Trim();
        var http = new HttpClient { BaseAddress = new Uri(opts.StateBaseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            """
            Usage: DataExportRelay --dsn-env <ENV_VAR_NAME> --state-base-url <url>
                     --state-token-file <path> [--execute]

            Defaults to a dry run: it lists the OPEN rows it WOULD relay and posts nothing.
            The DSN is read from an environment variable so it never lands in argv.
            """);

    private sealed record Options(
        string ConnectionString, string StateBaseUrl, string TokenFile, bool Execute)
    {
        public static Options Parse(string[] args)
        {
            string? dsnEnv = null, baseUrl = null, tokenFile = null;
            var execute = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--dsn-env": dsnEnv = Next(args, ref i); break;
                    case "--state-base-url": baseUrl = Next(args, ref i); break;
                    case "--state-token-file": tokenFile = Next(args, ref i); break;
                    case "--execute": execute = true; break;
                    default: throw new ArgumentException($"unknown argument '{args[i]}'");
                }
            }

            var dsn = Environment.GetEnvironmentVariable(
                dsnEnv ?? throw new ArgumentException("--dsn-env is required"));
            if (string.IsNullOrWhiteSpace(dsn))
                throw new ArgumentException($"environment variable '{dsnEnv}' is empty");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("--state-base-url is required");
            if (string.IsNullOrWhiteSpace(tokenFile) || !File.Exists(tokenFile))
                throw new ArgumentException("--state-token-file must point at a readable file");

            return new Options(dsn, baseUrl, tokenFile, execute);
        }

        private static string Next(string[] args, ref int i) =>
            ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for '{args[i - 1]}'");
    }
}
