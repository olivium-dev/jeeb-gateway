using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Push;

/// <summary>
/// Postgres-backed <see cref="IDeviceTokenStore"/> (T-backend-022 durability
/// follow-up).
///
/// Replaces <see cref="InMemoryDeviceTokenStore"/> in production. Registration
/// is idempotent on <c>(user_id, token)</c> via <c>UNIQUE(user_id, token)</c> +
/// <c>INSERT … ON CONFLICT … DO UPDATE</c> — the same idempotency shape as
/// <see cref="JeebGateway.Financials.PostgresSettlementStore"/> — so a device
/// re-registering (app reinstall, token-refresh re-send, retry after a flaky
/// network) upserts its one row instead of accumulating duplicates.
///
/// Unregister is a soft-delete: <c>revoked_at</c> is stamped and the row is
/// kept (not DELETEd), preserving revocation history. Reads only ever
/// surface non-revoked rows, which mirrors <see cref="InMemoryDeviceTokenStore"/>
/// exactly — that store hard-removes on unregister, so its reads only ever
/// return what wasn't removed either.
/// </summary>
public sealed class PostgresDeviceTokenStore : IDeviceTokenStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresDeviceTokenStore> _log;

    public PostgresDeviceTokenStore(INpgsqlConnectionFactory db, ILogger<PostgresDeviceTokenStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<IReadOnlyList<DeviceToken>> GetForUserAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT user_id, token, platform
            FROM device_tokens
            WHERE user_id = @UserId AND revoked_at IS NULL
            ORDER BY created_at ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);

        var results = new List<DeviceToken>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    public async Task RegisterAsync(DeviceToken token, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO device_tokens (id, user_id, token, platform, created_at, updated_at, revoked_at)
            VALUES (gen_random_uuid(), @UserId, @Token, @Platform, now(), now(), NULL)
            ON CONFLICT (user_id, token) DO UPDATE
                SET platform   = EXCLUDED.platform,
                    updated_at = now(),
                    revoked_at = NULL
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", token.UserId);
        cmd.Parameters.AddWithValue("Token", token.Token);
        cmd.Parameters.AddWithValue("Platform", token.Platform.ToString());
        await cmd.ExecuteNonQueryAsync(ct);

        _log.LogInformation(
            "Device token registered userId={UserId} platform={Platform}",
            token.UserId, token.Platform);
    }

    public async Task UnregisterAsync(string userId, string token, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE device_tokens
            SET revoked_at = now(), updated_at = now()
            WHERE user_id = @UserId AND token = @Token AND revoked_at IS NULL
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Token", token);
        var rows = await cmd.ExecuteNonQueryAsync(ct);

        if (rows > 0)
        {
            _log.LogInformation("Device token revoked userId={UserId}", userId);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static DeviceToken MapRow(NpgsqlDataReader r)
    {
        var platformText = r.GetString(r.GetOrdinal("platform"));
        return new DeviceToken(
            UserId: r.GetString(r.GetOrdinal("user_id")),
            Platform: Enum.Parse<DevicePlatform>(platformText, ignoreCase: true),
            Token: r.GetString(r.GetOrdinal("token")));
    }
}
