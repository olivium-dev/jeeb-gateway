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
