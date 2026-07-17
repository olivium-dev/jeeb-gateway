using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Infrastructure;
using Npgsql;

namespace JeebGateway.Partner;

/// <summary>
/// Postgres-backed <see cref="IPartnerOtpChallengeStore"/> (partner-wallet-bff PP-7). Persists to
/// <c>partner_otp_challenges</c> (migration 0041) so a minted step-up code — and, crucially, its
/// single-use consumption — survives a gateway bounce and is visible across replicas.
///
/// <para>Reuses the sibling money-store idiom byte-for-byte: raw Npgsql over the shared
/// <see cref="INpgsqlConnectionFactory"/> (see <see cref="PostgresPartnerWalletOperationStore"/>).
/// The money-critical single-use guarantee is a DB-level conditional UPDATE
/// (<c>SET consumed_at = now() WHERE id = @Id AND consumed_at IS NULL RETURNING id</c>): under a
/// concurrent double-submit only one caller's UPDATE returns a row, so a challenge authorizes AT MOST
/// ONE money move. The SHA-256 hash comparison is CONSTANT-TIME
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>); the raw code is never stored or logged.</para>
/// </summary>
public sealed class PostgresPartnerOtpChallengeStore : IPartnerOtpChallengeStore
{
    // Amounts are stored NUMERIC(18,4); compare the confirm's amount to the stored one at that
    // precision (half of the 4th-decimal ulp) so a double round-trip never spuriously mismatches.
    private const double AmountEpsilon = 0.00005d;

    private readonly INpgsqlConnectionFactory _db;

    public PostgresPartnerOtpChallengeStore(INpgsqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<Guid> IssueAsync(
        Guid partnerId, Guid jeeberId, double amount, string codeHash, DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        var id = Guid.NewGuid();

        const string sql = """
            INSERT INTO partner_otp_challenges (
                id, partner_id, jeeber_id, amount, code_hash, attempts, expires_at
            ) VALUES (
                @Id, @PartnerId, @JeeberId, @Amount, @CodeHash, 0, @ExpiresAt
            )
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("PartnerId", partnerId);
        cmd.Parameters.AddWithValue("JeeberId", jeeberId);
        cmd.Parameters.AddWithValue("Amount", amount);
        cmd.Parameters.AddWithValue("CodeHash", codeHash);
        cmd.Parameters.AddWithValue("ExpiresAt", expiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<PartnerOtpValidation> ValidateAndConsumeAsync(
        Guid challengeId, Guid partnerId, Guid jeeberId, double amount, string codeHash,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // Read the challenge to classify the verdict + get the stored hash for a constant-time compare.
        // The money-critical mutation (consume) is a separate CONDITIONAL update below, so this read is
        // only for classification — never the single-use gate itself.
        const string selectSql = """
            SELECT partner_id, jeeber_id, amount, code_hash, attempts, expires_at, consumed_at
            FROM partner_otp_challenges
            WHERE id = @Id
            LIMIT 1
            """;

        Guid storedPartner;
        Guid storedJeeber;
        double storedAmount;
        string storedHash;
        int attempts;
        DateTimeOffset expiresAt;
        bool consumed;

        await using (var selectCmd = new NpgsqlCommand(selectSql, conn))
        {
            selectCmd.Parameters.AddWithValue("Id", challengeId);
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return new PartnerOtpValidation(PartnerOtpOutcome.NotFound, 0);
            }

            storedPartner = reader.GetGuid(0);
            storedJeeber = reader.GetGuid(1);
            storedAmount = reader.GetDouble(2);
            storedHash = reader.GetString(3);
            attempts = reader.GetInt32(4);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(5);
            consumed = !reader.IsDBNull(6);
        }

        if (storedPartner != partnerId || storedJeeber != jeeberId
            || Math.Abs(storedAmount - amount) >= AmountEpsilon)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Mismatch, 0);
        }

        if (consumed)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0);
        }

        if (DateTimeOffset.UtcNow >= expiresAt)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Expired, 0);
        }

        if (attempts >= PartnerOtpChallengePolicy.MaxAttempts)
        {
            return new PartnerOtpValidation(PartnerOtpOutcome.Exhausted, 0);
        }

        if (!HashEquals(storedHash, codeHash))
        {
            // Count the wrong guess atomically (only while still unconsumed). RETURNING gives the new
            // attempts count so we report attempts-remaining without a re-read.
            const string bumpSql = """
                UPDATE partner_otp_challenges
                SET attempts = attempts + 1
                WHERE id = @Id AND consumed_at IS NULL
                RETURNING attempts
                """;
            await using var bumpCmd = new NpgsqlCommand(bumpSql, conn);
            bumpCmd.Parameters.AddWithValue("Id", challengeId);
            var newAttempts = await bumpCmd.ExecuteScalarAsync(ct);
            if (newAttempts is null)
            {
                // Consumed by a concurrent confirm between the read and this update.
                return new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0);
            }

            var remaining = Math.Max(0, PartnerOtpChallengePolicy.MaxAttempts - Convert.ToInt32(newAttempts));
            return new PartnerOtpValidation(PartnerOtpOutcome.WrongCode, remaining);
        }

        // Correct code — CONSUME atomically. The `consumed_at IS NULL` guard + RETURNING is the
        // single-use gate: a concurrent double-submit loses (no row) and is refused as consumed, so one
        // code authorizes at most one money move.
        const string consumeSql = """
            UPDATE partner_otp_challenges
            SET consumed_at = now()
            WHERE id = @Id AND consumed_at IS NULL
            RETURNING id
            """;
        await using var consumeCmd = new NpgsqlCommand(consumeSql, conn);
        consumeCmd.Parameters.AddWithValue("Id", challengeId);
        var won = await consumeCmd.ExecuteScalarAsync(ct) is not null;
        return won
            ? new PartnerOtpValidation(PartnerOtpOutcome.Valid, 0)
            : new PartnerOtpValidation(PartnerOtpOutcome.Consumed, 0);
    }

    /// <summary>Constant-time comparison of two SHA-256 hex hashes (equal length by construction).</summary>
    private static bool HashEquals(string storedHash, string presentedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(presentedHash));
}
