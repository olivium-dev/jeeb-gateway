namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Tunables for the OTP handover flow (T-backend-015 / JEEB-33).
///
/// Two failure modes drive an admin escalation:
/// <list type="bullet">
///   <item>The Jeeber submits <see cref="MaxAttempts"/> wrong OTPs in a
///     row — the OTP is locked and the row is escalated immediately.</item>
///   <item>The Jeeber flags the Client as unreachable at drop-off — the
///     <c>OtpHandoverSweeper</c> escalates the row once
///     <c>now - ClientUnreachableAt &gt;= ClientUnreachableWindow</c>.</item>
/// </list>
///
/// Pulled from configuration so the 3-strike threshold and 15-min timer
/// can be tightened in production without redeploying.
/// </summary>
public class OtpHandoverOptions
{
    public const string SectionName = "OtpHandover";

    /// <summary>
    /// Maximum consecutive wrong OTP submissions before the row locks
    /// out and is escalated. Inclusive — the third wrong attempt is the
    /// one that triggers the lockout, matching the AC.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// How long the unreachable-client flag may sit on a row before the
    /// sweeper escalates it to an admin (AC: "15-min unreachable →
    /// escalate").
    /// </summary>
    public TimeSpan ClientUnreachableWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Cadence of the <c>OtpHandoverSweeper</c>'s scan for due unreachable
    /// rows. Kept short so the 15-min escalation is precise to within
    /// one sweep interval.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
