using System.Text.Json;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Users;

/// <summary>
/// Durable identity <b>projection</b> for the gateway users store (JEB users-durable).
///
/// <para>The gateway is a stateless BFF; user-management (UM) stays the identity
/// system of record. But admin user-search (<c>GET /admin/users/search</c>) and the
/// token-mint <c>active_role</c> read were served purely from process memory
/// (<see cref="InMemoryUsersStore"/>), so every gateway bounce / replica move emptied
/// them — a searched-for user vanished until they logged in again. This store persists
/// the UM-resolved identity projection into the EXISTING <c>users</c> table
/// (migrations 0001 + 0006 + 0012, plus <c>active_role</c>/<c>role_switched_at</c> from
/// 0025) so the projection survives a restart. It is written best-effort ALONGSIDE the
/// in-memory store (which remains the permissive in-process behavioural home); UM
/// remains authoritative for identity.</para>
///
/// <para>Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/> exactly:
/// raw Npgsql over <see cref="INpgsqlConnectionFactory"/>, <c>AddWithValue</c> param
/// binding, <c>INSERT … ON CONFLICT (id) DO UPDATE</c> idempotency, and a private
/// <c>MapRow</c>/<c>ReadListAsync</c> pair.</para>
///
/// <para><b>Upsert semantics (<see cref="UpsertIdentityAsync"/>).</b> Identity fields
/// user-management owns (roles / active_role / language / phone) are replaced by the
/// incoming projection; DISPLAY fields (name / email / avatar_url) are blank-preserving
/// — an incoming blank never wipes a display value the profile-update mirror or /me
/// hydration already learned (the jeeberName-gap invariant, matching
/// <see cref="InMemoryUsersStore.UpsertProjectionAsync"/>). Suspension, rating and
/// created_at are left UNTOUCHED on conflict so a re-login never un-suspends a user or
/// clobbers the score-service's denormalised rating. Suspension is mutated durably only
/// through <see cref="SetSuspensionAsync"/>; PII is purged through
/// <see cref="PurgePiiAsync"/>.</para>
/// </summary>
public interface IUserProjectionStore
{
    /// <summary>Point-lookup of the durable projection row, or null when absent.</summary>
    Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Admin user-search served from Postgres via case-insensitive ILIKE on
    /// name / phone / email (the trigram indexes built in migration 0006), paginated.
    /// Survives a gateway restart — the whole point of the durable projection.
    /// </summary>
    Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct);

    /// <summary>
    /// Idempotent upsert of the UM-resolved identity projection (blank-preserving
    /// display; suspension / rating / created_at preserved on conflict).
    /// </summary>
    Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct);

    /// <summary>Durably flips suspension state (kept constraint-consistent internally).</summary>
    Task SetSuspensionAsync(
        string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct);

    /// <summary>Durable GDPR PII purge — name → '', email → NULL, avatar_url → NULL.</summary>
    Task PurgePiiAsync(string userId, CancellationToken ct);
}

/// <inheritdoc cref="IUserProjectionStore"/>
public sealed class PostgresUserProjectionStore : IUserProjectionStore
{
    private const string Columns =
        "id, phone, email, name, avatar_url, roles, language, rating, rating_count, " +
        "is_suspended, suspension_reason, suspended_at, suspended_by, " +
        "active_role, role_switched_at, created_at, updated_at";

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresUserProjectionStore> _log;

    public PostgresUserProjectionStore(INpgsqlConnectionFactory db, ILogger<PostgresUserProjectionStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        // users.id is a UUID column — a non-UUID id (e.g. the phone-keyed identity the
        // OTP UM-down fallback mints) simply has no durable projection row.
        if (!Guid.TryParse(userId, out var id)) return null;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT " + Columns + " FROM users WHERE id = @Id LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);

        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
    {
        var page = Math.Max(query.Page, 1);
        var size = Math.Clamp(query.PageSize, 1, 100);

        const string where =
            "WHERE (@Name  IS NULL OR name        ILIKE @Name)\n" +
            "  AND (@Phone IS NULL OR phone       ILIKE @Phone)\n" +
            "  AND (@Email IS NULL OR email::text ILIKE @Email)";

        await using var conn = await _db.OpenAsync(ct);

        // Total (pre-pagination) — the admin grid shows the full match count.
        int total;
        await using (var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM users\n{where}", conn))
        {
            AddFilterParams(countCmd, query);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var pageSql =
            $"SELECT {Columns} FROM users\n{where}\n" +
            "ORDER BY created_at DESC\n" +
            "LIMIT @Limit OFFSET @Offset";
        await using var pageCmd = new NpgsqlCommand(pageSql, conn);
        AddFilterParams(pageCmd, query);
        pageCmd.Parameters.AddWithValue("Limit", size);
        pageCmd.Parameters.AddWithValue("Offset", (page - 1) * size);

        var items = await ReadListAsync(pageCmd, ct);
        return new UserSearchResult { Items = items, Total = total };
    }

    public async Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct)
    {
        if (!Guid.TryParse(profile.Id, out var id)) return; // no durable row for non-UUID ids

        await using var conn = await _db.OpenAsync(ct);

        // Display fields are blank-preserving; roles/active_role/language/phone are
        // replaced (UM owns them); suspension/rating/created_at are left untouched.
        const string sql = """
            INSERT INTO users (
                id, phone, email, name, avatar_url,
                roles, language, active_role, role_switched_at,
                created_at, updated_at
            ) VALUES (
                @Id, @Phone, @Email::citext, @Name, @AvatarUrl,
                @Roles::jsonb, @Language, @ActiveRole, @RoleSwitchedAt,
                now(), now()
            )
            ON CONFLICT (id) DO UPDATE SET
                phone            = COALESCE(NULLIF(btrim(EXCLUDED.phone), ''), users.phone),
                email            = COALESCE(NULLIF(btrim(EXCLUDED.email::text), '')::citext, users.email),
                name             = COALESCE(NULLIF(btrim(EXCLUDED.name), ''), users.name),
                avatar_url       = COALESCE(NULLIF(btrim(EXCLUDED.avatar_url), ''), users.avatar_url),
                roles            = EXCLUDED.roles,
                language         = EXCLUDED.language,
                active_role      = EXCLUDED.active_role,
                role_switched_at = COALESCE(EXCLUDED.role_switched_at, users.role_switched_at),
                updated_at       = now()
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        // F1: bind NULL (not '') for a phone-less UM profile. An empty string
        // passes the column's NOT-NULL default but FAILS the users_phone_format
        // regex CHECK, so the INSERT threw for every email-login / super-login /
        // UM-cold-hydration projection (0027 drops the NOT NULL; NULL satisfies
        // the CHECK and is distinct under users_phone_uniq). The blank-preserving
        // COALESCE(NULLIF(btrim(EXCLUDED.phone),''), users.phone) on the ON
        // CONFLICT path still backfills the real phone on a later phone-OTP login.
        cmd.Parameters.AddWithValue(
            "Phone",
            string.IsNullOrWhiteSpace(profile.Phone) ? (object)DBNull.Value : profile.Phone);
        cmd.Parameters.AddWithValue("Email", (object?)profile.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Name", profile.Name ?? string.Empty);
        cmd.Parameters.AddWithValue("AvatarUrl", (object?)profile.AvatarUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Roles", JsonSerializer.Serialize(profile.Roles ?? new List<string>()));
        cmd.Parameters.AddWithValue("Language", string.IsNullOrWhiteSpace(profile.Language) ? "en" : profile.Language);
        cmd.Parameters.AddWithValue("ActiveRole", string.IsNullOrWhiteSpace(profile.ActiveRole) ? "customer" : profile.ActiveRole);
        cmd.Parameters.AddWithValue("RoleSwitchedAt", (object?)profile.RoleSwitchedAt ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSuspensionAsync(
        string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var id)) return;

        // Keep the users_suspension_state_consistency CHECK (0012) satisfied regardless
        // of caller inputs: suspended ⇒ reason+timestamp non-null; active ⇒ all null.
        // suspended_by is intentionally left NULL durably — it is a UUID FK to users(id)
        // and the acting admin may not be projected; the authoritative actor is kept on
        // the in-memory row the admin controller reads for its response/audit.
        object reasonParam = isSuspended ? (object)(reason ?? string.Empty) : DBNull.Value;
        object atParam = isSuspended ? (object)(at ?? DateTimeOffset.UtcNow) : DBNull.Value;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE users SET
                is_suspended      = @IsSuspended,
                suspension_reason = @Reason,
                suspended_at      = @At,
                suspended_by      = NULL,
                updated_at        = now()
            WHERE id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("IsSuspended", isSuspended);
        cmd.Parameters.AddWithValue("Reason", reasonParam);
        cmd.Parameters.AddWithValue("At", atParam);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PurgePiiAsync(string userId, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var id)) return;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE users SET
                name       = '',
                email      = NULL,
                avatar_url = NULL,
                updated_at = now()
            WHERE id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Binds the three optional name/phone/email search filters with an EXPLICIT
    /// <see cref="NpgsqlDbType.Text"/> type. A blank filter binds a text-typed NULL
    /// so the <c>(@X IS NULL OR col ILIKE @X)</c> arm short-circuits.
    ///
    /// <para><b>JEBV4-313.</b> The prior binding used <c>AddWithValue(name, DBNull.Value)</c>,
    /// which sends an UNTYPED NULL parameter. With every filter blank (the CMS "list all"
    /// call) Postgres could not infer the parameter type from <c>$n IS NULL</c> and threw
    /// <c>42P08 could not determine data type of parameter</c>, surfacing as a 500 on
    /// <c>GET /admin/users/search</c>. Pinning the parameter type to <c>text</c> removes the
    /// inference dependency (equivalent to writing <c>NULL::text</c> in raw SQL).</para>
    /// </summary>
    private static void AddFilterParams(NpgsqlCommand cmd, UserSearchQuery query)
    {
        AddTextFilter(cmd, "Name", query.Name);
        AddTextFilter(cmd, "Phone", query.Phone);
        AddTextFilter(cmd, "Email", query.Email);
    }

    private static void AddTextFilter(NpgsqlCommand cmd, string name, string? needle)
        => cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(needle) ? DBNull.Value : "%" + needle.Trim() + "%"
        });

    private static async Task<List<UserProfile>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<UserProfile>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static UserProfile MapRow(NpgsqlDataReader r)
    {
        var rolesText = r.IsDBNull(r.GetOrdinal("roles"))
            ? "[]"
            : r.GetString(r.GetOrdinal("roles"));

        return new UserProfile
        {
            Id            = r.GetGuid(r.GetOrdinal("id")).ToString(),
            // F1: phone is now nullable (0027) for phone-less UM profiles — read a
            // NULL back as "" so the non-null UserProfile.Phone contract holds.
            Phone         = r.IsDBNull(r.GetOrdinal("phone")) ? string.Empty : r.GetString(r.GetOrdinal("phone")),
            Email         = r.IsDBNull(r.GetOrdinal("email")) ? null : r.GetString(r.GetOrdinal("email")),
            Name          = r.GetString(r.GetOrdinal("name")),
            AvatarUrl     = r.IsDBNull(r.GetOrdinal("avatar_url")) ? null : r.GetString(r.GetOrdinal("avatar_url")),
            Roles         = DeserializeRoles(rolesText),
            Language      = r.GetString(r.GetOrdinal("language")),
            Rating        = r.IsDBNull(r.GetOrdinal("rating")) ? null : r.GetDecimal(r.GetOrdinal("rating")),
            RatingCount   = r.GetInt32(r.GetOrdinal("rating_count")),
            IsSuspended   = r.GetBoolean(r.GetOrdinal("is_suspended")),
            SuspensionReason = r.IsDBNull(r.GetOrdinal("suspension_reason"))
                ? null
                : r.GetString(r.GetOrdinal("suspension_reason")),
            SuspendedAt   = r.IsDBNull(r.GetOrdinal("suspended_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("suspended_at")),
            SuspendedBy   = r.IsDBNull(r.GetOrdinal("suspended_by"))
                ? null
                : r.GetGuid(r.GetOrdinal("suspended_by")).ToString(),
            ActiveRole    = r.GetString(r.GetOrdinal("active_role")),
            RoleSwitchedAt = r.IsDBNull(r.GetOrdinal("role_switched_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("role_switched_at")),
            CreatedAt     = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
            UpdatedAt     = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at")),
        };
    }

    private static List<string> DeserializeRoles(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
