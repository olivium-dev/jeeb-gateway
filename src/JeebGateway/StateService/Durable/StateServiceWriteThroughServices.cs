using JeebGateway.Services.Clients;

namespace JeebGateway.StateService.Durable;

// ---------------------------------------------------------------------------
// Durable write-through helpers for the user-scoped relational state that the
// state-service persists but does not (yet) expose a read-by-domain-key query
// for: refresh families (R2), KYC (R3), ratings (R4), disputes (R5).
//
// These services mirror each WRITE into jeeb-state-service so the durable row
// survives a gateway bounce. The gateway keeps all SEMANTICS (reuse-detection,
// mutual-blind reveal, version-checked transition) in its Domain/Services and
// its in-memory store remains the fast read/query model.
//
// CONTRACT GAP (reported, not worked around): the state-service is keyed by
// its own opaque ids (familyId / submissionId / caseId) and does NOT offer a
// GET by subject / tokenHash / deliveryId / contextId. So the gateway cannot
// REBUILD its in-memory index from the state-service after a bounce for these
// four domains. R1 (idempotency, GET by key) and R8 (locks/rate-limit, keyed
// by lockKey/bucket) are unaffected and fully bounce-survivable.
//
// FIX TRACKED: docs/adr/0006-gateway-in-memory-store-migration-to-state-service.md
// proposes the single generic primitive that closes this gap —
// GET /state/rows?owner&prefix (list by owner/prefix) — after which the R2–R5
// read-indexes can be rebuilt from the state-service instead of held in-memory.
// ---------------------------------------------------------------------------

/// <summary>R2: durable mirror of refresh-family create + rotate (reuse-detect
/// + family revocation are enforced server-side, so the security-critical
/// revocation now survives a bounce even though identity reconstruction does
/// not — see gap above).</summary>
public interface IStateRefreshFamilyWriter
{
    Task CreateFamilyAsync(string subject, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>Rotate presented→new. Returns the durable outcome so the
    /// gateway can branch reuse-detection on it.</summary>
    Task<RefreshRotateOutcome> RotateAsync(string presentedTokenHash, string newTokenHash, DateTimeOffset newExpiresAt, CancellationToken ct);
}

public enum RefreshRotateOutcome { Rotated, ReuseDetectedFamilyRevoked, NotFound, Unavailable }

public sealed class StateServiceRefreshFamilyWriter : IStateRefreshFamilyWriter
{
    private readonly IJeebStateServiceClient _client;
    private readonly ILogger<StateServiceRefreshFamilyWriter> _logger;

    public StateServiceRefreshFamilyWriter(IJeebStateServiceClient client, ILogger<StateServiceRefreshFamilyWriter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task CreateFamilyAsync(string subject, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await _client.CreateRefreshFamilyAsync(new FamilyCreateRequest
        {
            Subject = subject,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt
        }, ct);
    }

    public async Task<RefreshRotateOutcome> RotateAsync(string presentedTokenHash, string newTokenHash, DateTimeOffset newExpiresAt, CancellationToken ct)
    {
        try
        {
            await _client.RotateRefreshTokenAsync(new RotateRequest
            {
                PresentedTokenHash = presentedTokenHash,
                NewTokenHash = newTokenHash,
                NewExpiresAt = newExpiresAt
            }, ct);
            return RefreshRotateOutcome.Rotated;
        }
        catch (Exception ex) when (StateServiceErrors.IsConflict(ex))
        {
            return RefreshRotateOutcome.ReuseDetectedFamilyRevoked;
        }
        catch (Exception ex) when (StateServiceErrors.IsNotFound(ex))
        {
            return RefreshRotateOutcome.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh-family rotate unavailable; degrading to local store");
            return RefreshRotateOutcome.Unavailable;
        }
    }
}

/// <summary>R3: durable mirror of KYC create + status patch. The state-service
/// generates its own submissionId; the gateway records the durable row so a
/// draft survives a bounce (reconstruction by-subject is the reported gap).</summary>
public interface IStateKycWriter
{
    /// <summary>Persist a new KYC submission. The state-service assigns the id;
    /// the typed client does not echo it (OpenAPI 200/no-body), so the gateway
    /// keeps its own id as the cross-reference.</summary>
    Task CreateAsync(string subject, string status, object? payload, CancellationToken ct);
}

public sealed class StateServiceKycWriter : IStateKycWriter
{
    private readonly IJeebStateServiceClient _client;

    public StateServiceKycWriter(IJeebStateServiceClient client) => _client = client;

    public async Task CreateAsync(string subject, string status, object? payload, CancellationToken ct)
    {
        await _client.CreateKycAsync(new KycCreateRequest
        {
            Subject = subject,
            Status = status,
            Payload = payload
        }, ct);
    }
}

/// <summary>R4: durable mirror of rating submit + reveal (mutual-blind state
/// is the gateway's; the durable rows live in the state-service).</summary>
public interface IStateRatingWriter
{
    Task SubmitAsync(string contextId, string raterId, string rateeId, int score, string? comment, CancellationToken ct);
    Task RevealAsync(string contextId, CancellationToken ct);
}

public sealed class StateServiceRatingWriter : IStateRatingWriter
{
    private readonly IJeebStateServiceClient _client;

    public StateServiceRatingWriter(IJeebStateServiceClient client) => _client = client;

    public async Task SubmitAsync(string contextId, string raterId, string rateeId, int score, string? comment, CancellationToken ct)
    {
        await _client.SubmitRatingAsync(new RatingSubmitRequest
        {
            ContextId = contextId,
            RaterId = raterId,
            RateeId = rateeId,
            Score = score,
            Comment = comment
        }, ct);
    }

    public Task RevealAsync(string contextId, CancellationToken ct) =>
        _client.RevealRatingsAsync(contextId, ct);
}

/// <summary>R5: durable mirror of dispute open + version-checked transition
/// (the 409 version-conflict is enforced server-side, making concurrent
/// double-resolve race-safe in the durable store rather than a
/// ConcurrentDictionary).</summary>
public interface IStateDisputeWriter
{
    Task OpenAsync(string contextId, string openedBy, CancellationToken ct);

    /// <summary>Version-checked transition keyed by the state-service caseId.
    /// Returns false on 409 version conflict (lost the race).</summary>
    Task<bool> TransitionAsync(Guid caseId, string newStatus, int expectedVersion, string actor, string eventType, object? eventPayload, CancellationToken ct);
}

public sealed class StateServiceDisputeWriter : IStateDisputeWriter
{
    private readonly IJeebStateServiceClient _client;
    private readonly ILogger<StateServiceDisputeWriter> _logger;

    public StateServiceDisputeWriter(IJeebStateServiceClient client, ILogger<StateServiceDisputeWriter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task OpenAsync(string contextId, string openedBy, CancellationToken ct)
    {
        await _client.OpenDisputeAsync(new DisputeOpenRequest
        {
            ContextId = contextId,
            OpenedBy = openedBy
        }, ct);
    }

    public async Task<bool> TransitionAsync(Guid caseId, string newStatus, int expectedVersion, string actor, string eventType, object? eventPayload, CancellationToken ct)
    {
        try
        {
            await _client.TransitionDisputeAsync(caseId, new DisputeTransitionRequest
            {
                NewStatus = newStatus,
                ExpectedVersion = expectedVersion,
                Actor = actor,
                EventType = eventType,
                EventPayload = eventPayload
            }, ct);
            return true;
        }
        catch (Exception ex) when (StateServiceErrors.IsConflict(ex))
        {
            _logger.LogInformation("Dispute {CaseId} transition lost version race (expected {Version})", caseId, expectedVersion);
            return false;
        }
    }
}
