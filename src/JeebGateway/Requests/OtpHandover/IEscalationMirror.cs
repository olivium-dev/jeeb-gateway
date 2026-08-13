using Microsoft.Extensions.Logging;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// gwdbx W3-02 — best-effort dual-write of an escalation row to delivery-service.
///
/// <para><b>G-11 contract.</b> Callers MUST discard the returned task
/// (<c>_ = mirror.MirrorAsync(...)</c>). The 423 lockout response is produced from
/// the LOCAL <see cref="IAdminEscalationStore"/> row and must be byte-identical in
/// latency and outcome whether delivery-service is up, down, or slow. Implementations
/// must therefore never perform network I/O inline: they hand off and return.</para>
/// </summary>
public interface IEscalationMirror
{
    /// <summary>
    /// Hands <paramref name="row"/> off for upstream mirroring. Returns as soon as
    /// the hand-off is done — never after the upstream call. Never throws.
    /// </summary>
    Task MirrorAsync(AdminEscalation row, CancellationToken ct);
}

/// <summary>
/// Default no-op mirror: the ladder is at <c>local</c>, or delivery-service is unwired.
/// </summary>
public sealed class NoOpEscalationMirror : IEscalationMirror
{
    public Task MirrorAsync(AdminEscalation row, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Enforces the "never throws" clause above. <see cref="MirrorAsync"/> is NOT async, so a
/// throw happens synchronously, before any Task exists: <c>_ = MirrorAsync(...)</c> cannot
/// contain it. Consumers depend on THIS type, so every resolution — including a swapped-in
/// test double — is fail-open by construction rather than by every call site remembering.
/// </summary>
public sealed class FailOpenEscalationMirror : IEscalationMirror
{
    private readonly IEscalationMirror _inner;
    private readonly ILogger<FailOpenEscalationMirror> _log;

    public FailOpenEscalationMirror(IEscalationMirror inner, ILogger<FailOpenEscalationMirror> log)
    {
        _inner = inner;
        _log = log;
    }

    public Task MirrorAsync(AdminEscalation row, CancellationToken ct)
    {
        try
        {
            return _inner.MirrorAsync(row, ct);
        }
        catch (Exception ex)
        {
            // The local admin_escalations row is authoritative; the W3-07 backfill replays this id.
            _log.LogWarning(ex,
                "escalation mirror threw for escalationId={EscalationId} deliveryId={DeliveryId}; swallowed (G-11).",
                row?.Id, row?.DeliveryId);
            return Task.CompletedTask;
        }
    }
}
