using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using JeebGateway.service.ServiceUserManagement;
using Microsoft.Extensions.Logging;

namespace RoleServiceBackfill;

/// <summary>
/// Offline UM -> role-service backfill (prep-only, NOT run). Reads through the
/// gateway's own UM client surface; defaults to --dry-run (zero POSTs).
/// </summary>
public static class Program
{
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

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var log = loggerFactory.CreateLogger("RoleServiceBackfill");

        using var umHttp = new HttpClient { BaseAddress = new Uri(opts.UmBaseUrl) };
        var umClient = new ServiceUserManagementClient(opts.UmBaseUrl, umHttp);
        var dualRole = new HttpUserManagementDualRoleClient(
            umHttp, loggerFactory.CreateLogger<HttpUserManagementDualRoleClient>());

        using var roleHttp = new HttpClient { BaseAddress = new Uri(opts.RoleServiceBaseUrl) };
        roleHttp.DefaultRequestHeaders.Add("X-Api-Key", opts.RoleServiceApiKey);
        var roleService = new HttpRoleServiceClient(roleHttp, loggerFactory.CreateLogger<HttpRoleServiceClient>());

        var runner = new BackfillRunner(umClient, dualRole, roleService, log, opts);
        var summary = await runner.RunAsync(CancellationToken.None);

        Console.Error.WriteLine(
            $"[summary] users={summary.UsersEnumerated} umReadFailed={summary.UmReadFailed} "
            + $"grantErrors={summary.GrantErrors} activeRoleErrors={summary.ActiveRoleErrors} "
            + $"mismatches={summary.Mismatches} mode={(opts.Execute ? "execute" : "dry-run")}");

        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Usage: RoleServiceBackfill --um-base-url <url> --role-service-base-url <url>
                     --role-service-api-key-env <ENV_VAR_NAME> [--page-size 100]
                     [--resume-from-skip 0] [--execute]

            Defaults to --dry-run (enumerate + diff only, zero POSTs). --execute sends
            grant/active-role writes. --role-service-base-url has NO default in either
            mode (never dials the live MSI :10091 by accident).
            """);
    }
}

public sealed class Options
{
    public required string UmBaseUrl { get; init; }
    public required string RoleServiceBaseUrl { get; init; }
    public required string RoleServiceApiKey { get; init; }
    public int PageSize { get; init; } = 100;
    public int ResumeFromSkip { get; init; }
    public bool Execute { get; init; }

    public static Options Parse(string[] args)
    {
        string? um = null, roleBase = null, apiKeyEnv = null;
        var pageSize = 100;
        var resumeFrom = 0;
        var execute = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--um-base-url": um = RequireValue(args, ++i); break;
                case "--role-service-base-url": roleBase = RequireValue(args, ++i); break;
                case "--role-service-api-key-env": apiKeyEnv = RequireValue(args, ++i); break;
                case "--page-size": pageSize = int.Parse(RequireValue(args, ++i)); break;
                case "--resume-from-skip": resumeFrom = int.Parse(RequireValue(args, ++i)); break;
                case "--execute": execute = true; break;
                case "--dry-run": execute = false; break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (string.IsNullOrWhiteSpace(um))
            throw new ArgumentException("--um-base-url is required");
        // No baked-in default pointing at the live MSI :10091 — deliberate double-guard,
        // required explicitly in BOTH dry-run and --execute mode.
        if (string.IsNullOrWhiteSpace(roleBase))
            throw new ArgumentException("--role-service-base-url is required (no default, in either mode)");
        if (string.IsNullOrWhiteSpace(apiKeyEnv))
            throw new ArgumentException("--role-service-api-key-env is required");

        var apiKey = Environment.GetEnvironmentVariable(apiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException($"environment variable '{apiKeyEnv}' is unset or empty");

        return new Options
        {
            UmBaseUrl = um,
            RoleServiceBaseUrl = roleBase,
            RoleServiceApiKey = apiKey,
            PageSize = pageSize,
            ResumeFromSkip = resumeFrom,
            Execute = execute,
        };
    }

    private static string RequireValue(string[] args, int i)
    {
        if (i >= args.Length) throw new ArgumentException("missing value for the preceding flag");
        return args[i];
    }
}

public sealed class BackfillSummary
{
    public int UsersEnumerated;
    public int UmReadFailed;
    public int GrantErrors;
    public int ActiveRoleErrors;
    public int Mismatches;
}

/// <summary>Per-user JSONL report row (the reconciliation artifact an owner reviews).</summary>
public sealed record BackfillRow(
    string UserId,
    [property: JsonPropertyName("um_roles")] IReadOnlyList<string> UmRoles,
    [property: JsonPropertyName("um_active_role")] string? UmActiveRole,
    string Mode,
    [property: JsonPropertyName("grants")] IReadOnlyList<GrantOutcome>? Grants,
    [property: JsonPropertyName("active_role_set")] ActiveRoleOutcome? ActiveRoleSet,
    [property: JsonPropertyName("verify")] VerifyOutcome? Verify,
    string? Error);

public sealed record GrantOutcome(string RoleKey, string Outcome, string? ErrorCode);

public sealed record ActiveRoleOutcome(string RoleKey, string Outcome, string? ErrorCode);

public sealed record VerifyOutcome(
    bool Matched,
    [property: JsonPropertyName("role_service_roles")] IReadOnlyList<string> RoleServiceRoles,
    [property: JsonPropertyName("role_service_active_role")] string? RoleServiceActiveRole);

public sealed class BackfillRunner
{
    private const string AppId = "jeeb";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServiceUserManagementClient _um;
    private readonly IUserManagementDualRoleClient _dualRole;
    private readonly IRoleServiceClient _roleService;
    private readonly ILogger _log;
    private readonly Options _opts;

    public BackfillRunner(
        ServiceUserManagementClient um, IUserManagementDualRoleClient dualRole,
        IRoleServiceClient roleService, ILogger log, Options opts)
    {
        _um = um;
        _dualRole = dualRole;
        _roleService = roleService;
        _log = log;
        _opts = opts;
    }

    public async Task<BackfillSummary> RunAsync(CancellationToken ct)
    {
        var summary = new BackfillSummary();
        var skip = _opts.ResumeFromSkip;

        while (true)
        {
            var page = await _um.AllAsync(skip, _opts.PageSize, null, ct);
            var users = page.Users ?? Array.Empty<JeebGateway.service.ServiceUserManagement.UserProfileResponse>();
            if (users.Count == 0) break;

            foreach (var u in users)
            {
                if (string.IsNullOrWhiteSpace(u.UserId)) continue;
                summary.UsersEnumerated++;
                var row = await ProcessUserAsync(u.UserId, summary, ct);
                Console.WriteLine(JsonSerializer.Serialize(row, Json));
            }

            if (!page.HasMore) break;
            skip += _opts.PageSize;
        }

        return summary;
    }

    private async Task<BackfillRow> ProcessUserAsync(string userId, BackfillSummary summary, CancellationToken ct)
    {
        UserRolesResult? persisted;
        try
        {
            persisted = await _dualRole.GetUserRolesAsync(userId, ct);
        }
        catch (Exception ex)
        {
            persisted = null;
            _log.LogWarning(ex, "UM roles read failed for userId={UserId}", userId);
        }

        if (persisted is null)
        {
            // A 404/blip is logged and SKIPPED — never treated as "zero roles"
            // (would write a false-empty backfill row).
            summary.UmReadFailed++;
            return new BackfillRow(userId, Array.Empty<string>(), null, Mode(), null, null, null, "um_read_failed");
        }

        var umRoles = persisted.AvailableRoles;
        var umActive = persisted.ActiveRole;

        List<GrantOutcome>? grants = null;
        ActiveRoleOutcome? activeOutcome = null;

        if (_opts.Execute)
        {
            grants = new List<GrantOutcome>();
            foreach (var role in umRoles)
            {
                // GRANT BEFORE ACTIVE-ROLE — trg_active_role_is_held 409s otherwise.
                // Deterministic key: stable across reruns, never double-grants.
                var key = $"backfill:v1:{userId}:{role}:grant";
                try
                {
                    var result = await _roleService.GrantAsync(AppId, userId, role, "um-backfill", key, ct);
                    grants.Add(new GrantOutcome(role, result.Created ? "granted" : "no_op", null));
                }
                catch (RoleServiceCallException ex)
                {
                    // A real UM/role-service state divergence — logged, run continues.
                    summary.GrantErrors++;
                    grants.Add(new GrantOutcome(role, "error", ex.ErrorCode ?? ex.StatusCode.ToString()));
                }
            }

            if (!string.IsNullOrWhiteSpace(umActive) && umRoles.Contains(umActive, StringComparer.OrdinalIgnoreCase))
            {
                var key = $"backfill:v1:{userId}:active_role";
                try
                {
                    await _roleService.SetActiveRoleAsync(AppId, userId, umActive, "um-backfill", key, ct);
                    activeOutcome = new ActiveRoleOutcome(umActive, "set", null);
                }
                catch (RoleServiceCallException ex)
                {
                    summary.ActiveRoleErrors++;
                    activeOutcome = new ActiveRoleOutcome(umActive, "error", ex.ErrorCode ?? ex.StatusCode.ToString());
                }
            }
        }

        // Per-user verification (not just a total-row-count gate — N4): diff
        // role-service's current view against the UM source read above.
        VerifyOutcome verify;
        try
        {
            var subject = await _roleService.GetOrCreateAsync(AppId, userId, ct);
            var rsRoles = subject.Roles.Select(r => r.RoleKey).ToArray();
            var rsActive = subject.ActiveRole?.RoleKey;
            var matched = rsRoles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(umRoles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                && string.Equals(rsActive, umActive, StringComparison.OrdinalIgnoreCase);
            verify = new VerifyOutcome(matched, rsRoles, rsActive);
            if (!matched) summary.Mismatches++;
        }
        catch (Exception ex)
        {
            summary.Mismatches++;
            verify = new VerifyOutcome(false, Array.Empty<string>(), null);
            _log.LogWarning(ex, "role-service verify read failed for userId={UserId}", userId);
        }

        return new BackfillRow(userId, umRoles, umActive, Mode(), grants, activeOutcome, verify, null);
    }

    private string Mode() => _opts.Execute ? "execute" : "dry_run";
}
