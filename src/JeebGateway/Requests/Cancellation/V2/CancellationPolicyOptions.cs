namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// T-BE-030 (JEB-66) — thresholds and fee values for the v1 cancellation
/// policy surface (<c>POST /v1/deliveries/{id}/cancel</c>). Values are
/// pinned by Q-OPEN-2 / T-PO-002; the options pattern keeps them
/// out of source so PO can rotate the numbers without a redeploy via
/// <c>CancellationPolicy:*</c> in appsettings.
///
/// Defaults below are the Q-OPEN-2-locked values:
/// <list type="bullet">
///   <item>Client soft-limit 3/week — fee applies on the 4th+ cancel.</item>
///   <item>Client hard-limit 5/week — 6th+ cancel blocked with 429.</item>
///   <item>Client cancellation fee 15&#8239;000 LBP per Q-OPEN-2 (posted via
///     unified_payment_gateway POST /v1/payments/cod_jeeb/fee).</item>
///   <item>Jeeber strike threshold 3 in a rolling 30-day window — triggers
///     7-day suspension of the <c>jeeber</c> role on user-management.</item>
/// </list>
///
/// The window for the client soft/hard limit is an ISO-8601 week (Monday
/// 00:00 UTC → next Monday 00:00 UTC) so the 429's <c>retryAfter</c>
/// resets at a predictable boundary mobile clients can render as
/// "resets Monday".
/// </summary>
public sealed class CancellationPolicyOptions
{
    public const string SectionName = "CancellationPolicy";

    /// <summary>Soft limit — the (N+1)th client cancel inside a week
    /// pays the cancellation fee but is still allowed.</summary>
    public int ClientSoftLimitPerWeek { get; set; } = 3;

    /// <summary>Hard limit — any client cancel beyond this count inside a
    /// week is rejected with 429 + retryAfter.</summary>
    public int ClientHardLimitPerWeek { get; set; } = 5;

    /// <summary>Fee posted to unified_payment_gateway when the client
    /// breaches the soft limit. LBP (per Q-OPEN-2).</summary>
    public decimal ClientCancellationFeeLbp { get; set; } = 15_000m;

    /// <summary>Currency code recorded with the fee posting.</summary>
    public string Currency { get; set; } = "LBP";

    /// <summary>Strike count inside <see cref="JeeberStrikeWindow"/> that
    /// triggers a temporary suspension of the <c>jeeber</c> role.</summary>
    public int JeeberStrikeThreshold { get; set; } = 3;

    /// <summary>Rolling lookback for jeeber strikes.</summary>
    public TimeSpan JeeberStrikeWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Duration of the temporary jeeber-role suspension when the
    /// strike threshold trips.</summary>
    public TimeSpan JeeberRoleSuspensionDuration { get; set; } = TimeSpan.FromDays(7);
}
