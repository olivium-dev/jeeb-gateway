namespace JeebGateway.Requests;

/// <summary>
/// The frozen canonical SM-1 delivery state enum (ADR-002 §2.1).
///
/// This is the byte-for-byte mirror of delivery-service
/// <c>internal/status/status.go</c> <c>DeliveryStatus</c> and of the V3
/// requirements pack <c>03-state-machines.md</c> §SM-1. The string values are
/// the on-the-wire / audit-log tokens and MUST NOT drift from the Go side —
/// the parity test (PR-4, <c>DeliverySmParityTests</c>) fails the build if they do.
///
/// Seven runtime states live in the delivery SM. Pre-acceptance auction states
/// (<c>Broadcasting</c>, <c>OffersReceived</c>, <c>Accepted</c>) and the
/// request-lifecycle <c>Expired</c> token live in OTHER bounded contexts and are
/// deliberately NOT delivery states (ADR-002 §2.1). <see cref="Expired"/> is
/// frozen here only as a reserved canonical string so the gateway and
/// delivery-service agree on the spelling; it never appears in the
/// <see cref="DeliverySm"/> transition table.
/// </summary>
public static class CanonicalDeliveryStatus
{
    public const string Ordered = "Ordered";
    public const string Picked = "Picked";
    public const string InTransit = "InTransit";
    public const string AtDoor = "AtDoor";
    public const string Done = "Done";
    public const string Cancelled = "Cancelled";
    public const string FailedNeedsEscalation = "FailedNeedsEscalation";

    /// <summary>
    /// Reserved request-lifecycle terminal token (G-MISS-07 / S05). NOT a
    /// delivery state — a delivery row does not exist when a request expires.
    /// Frozen here only so the string is identical across services.
    /// </summary>
    public const string Expired = "Expired";

    /// <summary>The 7 states owned by the delivery SM (excludes <see cref="Expired"/>).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ordered, Picked, InTransit, AtDoor, Done, Cancelled, FailedNeedsEscalation
    };

    /// <summary>
    /// Absorbing states with no outgoing edge. Mirrors
    /// <c>status.go::terminalStates</c> exactly: <see cref="Done"/> and
    /// <see cref="Cancelled"/>. <see cref="FailedNeedsEscalation"/> is
    /// deliberately NON-terminal (admin-resolvable → Done / Cancelled).
    /// </summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Done, Cancelled
    };

    public static bool IsKnown(string status) => All.Contains(status);

    public static bool IsTerminal(string status) => Terminal.Contains(status);
}

/// <summary>
/// The frozen canonical trigger lexicon (ADR-002 §2.2, 9 triggers). Mirrors
/// <c>status.go</c> <c>Trigger</c> constants. The trigger is the business reason
/// for an edge and is the ONLY thing that disambiguates the two
/// <c>Ordered → Cancelled</c> edges (<see cref="ClientCancelNoFee"/> vs
/// <see cref="JeeberCancelStrike"/>) — a distinction the gateway's legacy
/// <c>(from,to)</c>-only model could not represent and which S13 penalty logic
/// requires.
/// </summary>
public static class DeliveryTrigger
{
    public const string JeeberTap = "jeeber_tap";
    public const string ClientCancelNoFee = "client_cancel_no_fee";
    public const string JeeberCancelStrike = "jeeber_cancel_strike";
    public const string JeeberCancelHighStrike = "jeeber_cancel_high_strike";
    public const string EscalateEither = "escalate_either";
    public const string OtpVerified = "otp_verified";
    public const string OtpFailOrJeeberEscalate = "otp_fail_or_jeeber_escalate";
    public const string AdminResolve = "admin_resolve";
    public const string AdminCancel = "admin_cancel";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        JeeberTap, ClientCancelNoFee, JeeberCancelStrike, JeeberCancelHighStrike,
        EscalateEither, OtpVerified, OtpFailOrJeeberEscalate, AdminResolve, AdminCancel
    };

    public static bool IsKnown(string trigger) => All.Contains(trigger);
}

/// <summary>
/// The frozen canonical source lexicon (ADR-002 §2.2, 4 sources). Mirrors
/// <c>status.go</c> <c>TriggerSource</c>. Stored alongside the trigger on every
/// audit row so a dispute (S14) records WHO initiated the edge, not only the
/// business reason.
/// </summary>
public static class DeliveryTriggerSource
{
    public const string Jeeber = "jeeber";
    public const string Client = "client";
    public const string Admin = "admin";
    public const string System = "system";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Jeeber, Client, Admin, System
    };

    public static bool IsKnown(string source) => All.Contains(source);
}
