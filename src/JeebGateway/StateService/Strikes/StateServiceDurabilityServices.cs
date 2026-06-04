using JeebGateway.Services.Clients;

namespace JeebGateway.StateService.Strikes;

/// <summary>
/// Durable write-through for the write-only state primitives (R6 strikes +
/// cancellation counters, R7 OTP-escalation). These are append/bump
/// operations whose durable record lives in jeeb-state-service; the gateway
/// keeps the policy semantics (strike thresholds, weekly window, escalation
/// ladder) in its own Domain/Services and mirrors the durable fact here so a
/// bounce no longer wipes the audit trail.
///
/// NOTE (contract gap): the state-service does not yet expose a read-by-subject
/// query for these rows, so the gateway cannot RECONSTRUCT its in-memory tally
/// from the state-service after a bounce — only the durable record is improved.
/// Full reconstruction needs GET-by-subject endpoints (see SPECS-STATUS).
/// </summary>
public interface IStateStrikeWriter
{
    /// <summary>Append an idempotent strike for a subject (R6 / S13).</summary>
    Task AddStrikeAsync(string subject, string reason, string idempotencyToken, CancellationToken ct);

    /// <summary>Bump the rolling weekly cancellation counter (R6 / S13).</summary>
    Task BumpCancellationAsync(string subject, DateOnly windowStart, CancellationToken ct);

    /// <summary>Record an OTP-escalation step for an identity (R7 / S09, S14).</summary>
    Task EscalateOtpAsync(string identity, int lockSeconds, CancellationToken ct);
}

public sealed class StateServiceStrikeWriter : IStateStrikeWriter
{
    private readonly IJeebStateServiceClient _client;
    private readonly ILogger<StateServiceStrikeWriter> _logger;

    public StateServiceStrikeWriter(IJeebStateServiceClient client, ILogger<StateServiceStrikeWriter> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task AddStrikeAsync(string subject, string reason, string idempotencyToken, CancellationToken ct)
    {
        await _client.AddStrikeAsync(new StrikeAddRequest
        {
            Subject = subject,
            Reason = reason,
            IdempotencyToken = idempotencyToken
        }, ct);
    }

    public async Task BumpCancellationAsync(string subject, DateOnly windowStart, CancellationToken ct)
    {
        await _client.BumpCancellationCounterAsync(new CancellationBumpRequest
        {
            Subject = subject,
            // The state-service expects a date-only window start.
            WindowStart = windowStart.ToDateTime(TimeOnly.MinValue)
        }, ct);
    }

    public async Task EscalateOtpAsync(string identity, int lockSeconds, CancellationToken ct)
    {
        await _client.EscalateOtpAsync(new OtpEscalateRequest
        {
            Identity = identity,
            LockSeconds = lockSeconds
        }, ct);
    }
}
