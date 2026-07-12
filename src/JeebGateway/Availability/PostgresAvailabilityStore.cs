using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Availability;

/// <summary>
/// Postgres-backed <see cref="IAvailabilityStore"/> (jeeb-gateway durability
/// hardening). Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s
/// raw-Npgsql shape (connection-per-call via <see cref="INpgsqlConnectionFactory"/>,
/// named parameters, reader-based row mapping). Replaces
/// <see cref="InMemoryAvailabilityStore"/> in production so the admin ops-map
/// (<see cref="JeebGateway.Controllers.AdminZonesController"/>, T-backend-051) and
/// the auto-offline sweeper (<see cref="AutoOfflineSweeper"/>, T-backend-023) survive
/// a gateway bounce / replica move instead of resetting to "everyone offline" on
/// every deploy.
///
/// <para><b>Scope — NOT the matching presence source of truth.</b>
/// <see cref="JeebGateway.Controllers.AvailabilityController"/>'s jeeber-facing
/// GET/PATCH wires THROUGH to delivery-service / heart-beat for presence (that is
/// what matching reads); this store only backs the admin ops-map read and the
/// sweeper's withdraw-offer accounting — two gateway-local reads with no upstream
/// equivalent. Swapping this store's backing from memory to Postgres therefore has
/// NO effect on matching.</para>
///
/// <para><b>Table reuse (never re-CREATE).</b> Persists to the EXISTING
/// <c>jeeber_availability</c> table (migration 0003, PostGIS) — <c>is_online</c>,
/// <c>vehicle_type</c>, <c>last_location</c>, and <c>last_seen_at</c> are reused
/// as-is. Migration 0026 ALTERs the table to add the two columns 0003 never
/// carried: <c>zone</c> (<see cref="GoOnlineRequest.Zone"/>, echoed back on every
/// read) and <c>last_interaction_at</c> (the sweeper's activity watermark, distinct
/// from <c>last_seen_at</c> — see <see cref="AutoOfflineSweeper"/>). <see
/// cref="ListOnlineAsync"/> reuses the existing <c>is_online</c> boolean — already
/// the basis of two 0003 partial indexes — rather than adding a second, competing
/// "status" column to a shared table.</para>
///
/// <para><b>Upsert shape.</b> Every write is a single
/// <c>INSERT … ON CONFLICT (user_id) DO UPDATE … RETURNING</c> round trip. Unlike
/// <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s insert-ONCE
/// idempotency (a settlement is written exactly once), availability is legitimately
/// flipped many times a day per Jeeber, so the shape here is insert-OR-flip. A
/// <c>previous</c> CTE (evaluated against the pre-statement snapshot, same trick
/// <c>PostgresSettlementStore.MarkReceiptGeneratedAsync</c>'s "changed?" RETURNING
/// check relies on) captures the PRE-image <c>is_online</c> in the SAME statement so
/// <see cref="GoOnlineResult.WasAlreadyOnline"/> / <see cref="GoOfflineResult.WasOnline"/>
/// are race-free without a separate SELECT.</para>
///
/// <para><b>CHECK constraint inherited from 0003.</b>
/// <c>jeeber_availability_online_requires_location</c> requires a non-null
/// <c>last_location</c> + <c>last_seen_at</c> whenever <c>is_online = TRUE</c>.
/// <see cref="GoOnlineAsync"/> always stamps <c>last_seen_at = now</c> and, via
/// <c>COALESCE(new point, existing point)</c>, keeps the previous location when the
/// request omits fresh coordinates — so only a Jeeber's very first-ever go-online
/// with NO coordinates at all could violate it. The mobile client always sends both
/// on go-online (matching needs them); this is a pre-existing schema invariant this
/// store neither weakens nor introduces. A violation surfaces as a thrown
/// <see cref="PostgresException"/>, which <see cref="JeebGateway.Controllers.AvailabilityController"/>'s
/// best-effort mirror wrapper already swallows (the authoritative upstream toggle
/// has already committed by the time the mirror runs).</para>
///
/// <para><b><see cref="GetAsync"/> is a pure read.</b> Unlike
/// <see cref="InMemoryAvailabilityStore"/>'s <c>ConcurrentDictionary.GetOrAdd</c>
/// (which seeds a row as a read side-effect), this store returns an unpersisted
/// default for an unknown user instead of inserting one. <see cref="GetAsync"/> has
/// no production caller today — RecordInteraction/GoOnline/GoOffline/ListOnline own
/// every write path (verified: no controller or background service calls
/// <c>IAvailabilityStore.GetAsync</c>) — so this is not an observable behavior
/// change, just a safer default for a method whose name promises a read.</para>
/// </summary>
public sealed class PostgresAvailabilityStore : IAvailabilityStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly IGeoIndex _geo;
    private readonly IPendingOffersStore _offers;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostgresAvailabilityStore> _log;

    public PostgresAvailabilityStore(
        INpgsqlConnectionFactory db,
        IGeoIndex geo,
        IPendingOffersStore offers,
        TimeProvider clock,
        ILogger<PostgresAvailabilityStore> log)
    {
        _db = db;
        _geo = geo;
        _offers = offers;
        _clock = clock;
        _log = log;
    }

    public async Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var row = await QuerySingleAsync(conn, Guid.Parse(userId), ct);
        return row ?? NewDefault(userId);
    }

    public async Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.GetUtcNow();
        var userGuid = Guid.Parse(userId);

        // previous: pre-image is_online, read against the snapshot as of the
        // START of this statement (before the INSERT/UPDATE below takes
        // effect) — the standard Postgres "capture old value in an upsert"
        // idiom. Zero prior rows => NULL => "was not already online".
        const string sql = """
            WITH previous AS (
                SELECT is_online FROM jeeber_availability WHERE user_id = @UserId
            )
            INSERT INTO jeeber_availability (
                user_id, is_online, vehicle_type, zone, last_location,
                last_seen_at, last_interaction_at, created_at, updated_at
            ) VALUES (
                @UserId, TRUE, @VehicleType::jeeber_vehicle_type, @Zone,
                CASE WHEN @Longitude IS NULL OR @Latitude IS NULL THEN NULL
                     ELSE ST_SetSRID(ST_MakePoint(@Longitude::double precision, @Latitude::double precision), 4326)::geography END,
                @Now, @Now, now(), now()
            )
            ON CONFLICT (user_id) DO UPDATE SET
                is_online           = TRUE,
                vehicle_type        = EXCLUDED.vehicle_type,
                zone                = EXCLUDED.zone,
                last_location       = COALESCE(EXCLUDED.last_location, jeeber_availability.last_location),
                last_seen_at        = @Now,
                last_interaction_at = @Now,
                updated_at          = now()
            RETURNING
                user_id, is_online, vehicle_type, zone,
                ST_X(last_location::geometry) AS longitude,
                ST_Y(last_location::geometry) AS latitude,
                last_seen_at, last_interaction_at, updated_at,
                (SELECT is_online FROM previous) AS was_online_before
            """;

        JeeberAvailability availability;
        bool wasAlreadyOnline;
        await using (var conn = await _db.OpenAsync(ct))
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("UserId", userGuid);
            cmd.Parameters.AddWithValue("VehicleType", request.VehicleType.ToWire());
            cmd.Parameters.AddWithValue("Zone", request.Zone);
            // Explicit NpgsqlDbType.Double (not AddWithValue): when Longitude/Latitude
            // are null, AddWithValue(..., DBNull.Value) has no CLR type to infer from
            // and Npgsql sends the parameter as "unknown", which Postgres cannot
            // resolve inside ST_MakePoint(@Longitude, @Latitude) — surfaces as a
            // thrown Npgsql 42P08 (ambiguous parameter) that AvailabilityController's
            // best-effort mirror wrapper swallows, silently leaving is_online stuck
            // at its previous value. Pinning the wire type removes the ambiguity
            // regardless of whether the value is present or DBNull.
            cmd.Parameters.Add(new NpgsqlParameter("Longitude", NpgsqlDbType.Double)
            {
                Value = (object?)request.Longitude ?? DBNull.Value
            });
            cmd.Parameters.Add(new NpgsqlParameter("Latitude", NpgsqlDbType.Double)
            {
                Value = (object?)request.Latitude ?? DBNull.Value
            });
            cmd.Parameters.AddWithValue("Now", now);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"go-online upsert for jeeber {userId} returned no row.");

            wasAlreadyOnline = !reader.IsDBNull(reader.GetOrdinal("was_online_before"))
                && reader.GetBoolean(reader.GetOrdinal("was_online_before"));
            availability = MapRow(reader);
        }

        // Durable row commits first, THEN the Redis geo index — same
        // ordering InMemoryAvailabilityStore uses (row/dict update, then
        // _geo.AddAsync). The Postgres connection is released before this
        // call (the `await using` block above already disposed it).
        await _geo.AddAsync(userId, availability.VehicleType, availability.Longitude, availability.Latitude, ct);

        _log.LogInformation(
            "Jeeber {UserId} availability upserted online (vehicle={VehicleType}, zone={Zone}, wasAlreadyOnline={WasAlreadyOnline})",
            userId, availability.VehicleType, availability.Zone, wasAlreadyOnline);

        return new GoOnlineResult
        {
            Availability = availability,
            WasAlreadyOnline = wasAlreadyOnline
        };
    }

    public async Task<GoOfflineResult> GoOfflineAsync(string userId, GoOfflineReason reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var userGuid = Guid.Parse(userId);

        // Mirrors InMemoryAvailabilityStore.GoOfflineAsync: LastInteractionAt
        // only advances for an explicit user toggle, never for an
        // auto-offline sweep (that is the sweeper acting ON inactivity, not
        // a fresh interaction).
        var isUserToggle = reason == GoOfflineReason.UserToggle;

        const string sql = """
            WITH previous AS (
                SELECT is_online FROM jeeber_availability WHERE user_id = @UserId
            )
            INSERT INTO jeeber_availability (
                user_id, is_online, vehicle_type, zone, last_location,
                last_seen_at, last_interaction_at, created_at, updated_at
            ) VALUES (
                @UserId, FALSE, @DefaultVehicleType::jeeber_vehicle_type, NULL, NULL, NULL,
                CASE WHEN @IsUserToggle THEN @Now ELSE NULL END, now(), now()
            )
            ON CONFLICT (user_id) DO UPDATE SET
                is_online           = FALSE,
                last_interaction_at = CASE WHEN @IsUserToggle THEN @Now ELSE jeeber_availability.last_interaction_at END,
                updated_at          = now()
            RETURNING
                user_id, is_online, vehicle_type, zone,
                ST_X(last_location::geometry) AS longitude,
                ST_Y(last_location::geometry) AS latitude,
                last_seen_at, last_interaction_at, updated_at,
                (SELECT is_online FROM previous) AS was_online_before
            """;

        JeeberAvailability availability;
        bool wasOnline;
        await using (var conn = await _db.OpenAsync(ct))
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("UserId", userGuid);
            cmd.Parameters.AddWithValue("DefaultVehicleType", VehicleType.Car.ToWire());
            cmd.Parameters.AddWithValue("IsUserToggle", isUserToggle);
            cmd.Parameters.AddWithValue("Now", now);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"go-offline upsert for jeeber {userId} returned no row.");

            wasOnline = !reader.IsDBNull(reader.GetOrdinal("was_online_before"))
                && reader.GetBoolean(reader.GetOrdinal("was_online_before"));
            availability = MapRow(reader);
        }

        // Same ordering InMemoryAvailabilityStore uses: durable row flips
        // offline first, THEN the geo index drops the member and any
        // in-flight offers are withdrawn.
        await _geo.RemoveAsync(userId, ct);
        var withdrawn = await _offers.WithdrawForJeeberAsync(userId, ct);

        _log.LogInformation(
            "Jeeber {UserId} availability upserted offline (reason={Reason}, wasOnline={WasOnline}, withdrawnOffers={Withdrawn})",
            userId, reason, wasOnline, withdrawn);

        return new GoOfflineResult
        {
            Availability = availability,
            WithdrawnOffers = withdrawn,
            WasOnline = wasOnline
        };
    }

    public async Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        var userGuid = Guid.Parse(userId);

        // Mirrors InMemoryAvailabilityStore.RecordInteractionAsync exactly:
        // touches ONLY last_interaction_at on an existing row (is_online,
        // vehicle_type, zone, location are all left untouched); seeds a
        // fresh offline default row the first time a never-seen userId
        // interacts (e.g. a GET from a Jeeber who has never gone online).
        const string sql = """
            INSERT INTO jeeber_availability (
                user_id, is_online, vehicle_type, zone, last_location,
                last_seen_at, last_interaction_at, created_at, updated_at
            ) VALUES (
                @UserId, FALSE, @DefaultVehicleType::jeeber_vehicle_type, NULL, NULL, NULL, @At, now(), now()
            )
            ON CONFLICT (user_id) DO UPDATE SET
                last_interaction_at = @At,
                updated_at          = now()
            """;

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userGuid);
        cmd.Parameters.AddWithValue("DefaultVehicleType", VehicleType.Car.ToWire());
        cmd.Parameters.AddWithValue("At", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct)
    {
        // The ticket's "WHERE status='online'" maps onto the EXISTING
        // is_online boolean (migration 0003) rather than a new column — see
        // migration 0026's header for why. Already covered by
        // jeeber_availability_online_vehicle_idx / _last_seen_idx (0003)
        // and jeeber_availability_last_interaction_idx (0026).
        const string sql = """
            SELECT user_id, is_online, vehicle_type, zone,
                   ST_X(last_location::geometry) AS longitude,
                   ST_Y(last_location::geometry) AS latitude,
                   last_seen_at, last_interaction_at, updated_at
            FROM jeeber_availability
            WHERE is_online = TRUE
            """;

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<JeeberAvailability>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<JeeberAvailability?> QuerySingleAsync(NpgsqlConnection conn, Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT user_id, is_online, vehicle_type, zone,
                   ST_X(last_location::geometry) AS longitude,
                   ST_Y(last_location::geometry) AS latitude,
                   last_seen_at, last_interaction_at, updated_at
            FROM jeeber_availability
            WHERE user_id = @UserId
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRow(reader) : null;
    }

    private JeeberAvailability NewDefault(string userId) => new()
    {
        UserId = userId,
        IsOnline = false,
        VehicleType = VehicleType.Car,
        Zone = null,
        UpdatedAt = _clock.GetUtcNow()
    };

    private static JeeberAvailability MapRow(NpgsqlDataReader r)
    {
        // Unmapped Postgres enum comes back over the wire as text — GetString
        // works with no NetTopologySuite / enum type-mapper plugin required
        // (same reason the writes above cast @Param::jeeber_vehicle_type in
        // SQL text rather than registering a global enum mapper).
        VehicleTypeExtensions.TryParseWire(r.GetString(r.GetOrdinal("vehicle_type")), out var vehicle);

        return new JeeberAvailability
        {
            UserId = r.GetGuid(r.GetOrdinal("user_id")).ToString(),
            IsOnline = r.GetBoolean(r.GetOrdinal("is_online")),
            VehicleType = vehicle,
            Zone = r.IsDBNull(r.GetOrdinal("zone")) ? null : r.GetString(r.GetOrdinal("zone")),
            Longitude = r.IsDBNull(r.GetOrdinal("longitude")) ? null : r.GetDouble(r.GetOrdinal("longitude")),
            Latitude = r.IsDBNull(r.GetOrdinal("latitude")) ? null : r.GetDouble(r.GetOrdinal("latitude")),
            LastSeenAt = r.IsDBNull(r.GetOrdinal("last_seen_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("last_seen_at")),
            LastInteractionAt = r.IsDBNull(r.GetOrdinal("last_interaction_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("last_interaction_at")),
            UpdatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at"))
        };
    }
}
