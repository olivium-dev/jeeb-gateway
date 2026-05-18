using System.Collections.Concurrent;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Gateway-side bookkeeping for the AC5b refresh-token-family rotation
/// pattern. The store is consulted on every <c>/v1/auth/refresh</c> call to
/// decide:
///   <list type="bullet">
///     <item>The presented JTI is still the active leaf of its family → rotate.</item>
///     <item>The presented JTI was already rotated → reuse detected; revoke the
///       entire family and force re-OTP.</item>
///     <item>The family was revoked → RevokedOrInvalid.</item>
///   </list>
///
/// The MVP implementation is in-memory; production wiring swaps to Postgres
/// (or Redis SET-IFNX) for horizontal scaling across gateway replicas, behind
/// the same interface. Issuance is non-async (single ConcurrentDictionary
/// write); rotation/revocation are async to match the Postgres swap.
/// </summary>
public enum RotateOutcome
{
    Rotated,
    Reused,
    FamilyRevoked,
}

public interface IRefreshTokenFamilyStore
{
    /// <summary>
    /// Record the issuance of a fresh refresh token leaf for a family. Called
    /// from <see cref="IJeebJwtIssuer.Issue"/> and from the rotated mint path
    /// in <see cref="IJeebJwtIssuer.RefreshAsync"/>.
    /// </summary>
    void RegisterIssued(Guid familyId, Guid jti, Guid userId, DateTimeOffset expiresAt);

    /// <summary>
    /// Attempt to rotate <paramref name="presentedJti"/> off the active leaf
    /// of <paramref name="familyId"/>. Returns:
    ///   <list type="bullet">
    ///     <item><see cref="RotateOutcome.Rotated"/> — caller may mint a new
    ///       pair under the same family id.</item>
    ///     <item><see cref="RotateOutcome.Reused"/> — the JTI was already
    ///       rotated; caller MUST revoke the whole family.</item>
    ///     <item><see cref="RotateOutcome.FamilyRevoked"/> — the family was
    ///       previously revoked; caller returns RevokedOrInvalid.</item>
    ///   </list>
    /// </summary>
    Task<RotateOutcome> TryRotateAsync(Guid familyId, Guid presentedJti, DateTimeOffset now, CancellationToken ct);

    Task RevokeFamilyAsync(Guid familyId, CancellationToken ct);
}

public sealed class InMemoryRefreshTokenFamilyStore : IRefreshTokenFamilyStore
{
    // Per-family lock objects — keeps the rotate/register/revoke critical
    // sections serialised without taking a process-wide lock.
    private readonly ConcurrentDictionary<Guid, object> _locks = new();
    private readonly ConcurrentDictionary<Guid, FamilyState> _families = new();

    public void RegisterIssued(Guid familyId, Guid jti, Guid userId, DateTimeOffset expiresAt)
    {
        var gate = _locks.GetOrAdd(familyId, _ => new object());
        lock (gate)
        {
            if (_families.TryGetValue(familyId, out var prev))
            {
                if (prev.Revoked)
                {
                    // Re-registration into a revoked family is a coding error.
                    // Issue() always creates a fresh familyId, so this branch
                    // implies a rotate that should never have been attempted.
                    throw new InvalidOperationException(
                        $"Cannot register a new JTI into revoked family {familyId}.");
                }
                var known = new HashSet<Guid>(prev.KnownJtis) { jti };
                _families[familyId] = prev with
                {
                    ActiveJti = jti,
                    ExpiresAt = expiresAt,
                    KnownJtis = known,
                };
            }
            else
            {
                _families[familyId] = new FamilyState(
                    UserId:    userId,
                    ActiveJti: jti,
                    ExpiresAt: expiresAt,
                    Revoked:   false,
                    KnownJtis: new HashSet<Guid> { jti });
            }
        }
    }

    public Task<RotateOutcome> TryRotateAsync(Guid familyId, Guid presentedJti, DateTimeOffset now, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(familyId, _ => new object());
        lock (gate)
        {
            if (!_families.TryGetValue(familyId, out var current))
            {
                return Task.FromResult(RotateOutcome.FamilyRevoked);
            }
            if (current.Revoked)
            {
                return Task.FromResult(RotateOutcome.FamilyRevoked);
            }

            if (current.ActiveJti == presentedJti)
            {
                // Rotation candidate — clear ActiveJti so a concurrent
                // RegisterIssued can write the replacement. The next
                // call presenting the SAME JTI now lands on the "known
                // but not active" branch and gets Reused.
                _families[familyId] = current with { ActiveJti = Guid.Empty };
                return Task.FromResult(RotateOutcome.Rotated);
            }

            if (current.KnownJtis.Contains(presentedJti))
            {
                return Task.FromResult(RotateOutcome.Reused);
            }
            return Task.FromResult(RotateOutcome.FamilyRevoked);
        }
    }

    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(familyId, _ => new object());
        lock (gate)
        {
            if (_families.TryGetValue(familyId, out var prev))
            {
                _families[familyId] = prev with { Revoked = true, ActiveJti = Guid.Empty };
            }
            else
            {
                _families[familyId] = new FamilyState(
                    UserId:    Guid.Empty,
                    ActiveJti: Guid.Empty,
                    ExpiresAt: DateTimeOffset.MinValue,
                    Revoked:   true,
                    KnownJtis: new HashSet<Guid>());
            }
        }
        return Task.CompletedTask;
    }

    private sealed record FamilyState(
        Guid UserId,
        Guid ActiveJti,
        DateTimeOffset ExpiresAt,
        bool Revoked,
        HashSet<Guid> KnownJtis);
}
