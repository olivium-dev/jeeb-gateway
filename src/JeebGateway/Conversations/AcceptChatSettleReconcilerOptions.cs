namespace JeebGateway.Conversations;

/// <summary>
/// GW5 / W1.6-gateway — tuning for <see cref="AcceptChatSettleReconciler"/>.
/// Safe defaults; no appsettings change is required to get the heal pass.
/// </summary>
public sealed class AcceptChatSettleReconcilerOptions
{
    public const string SectionName = "Conversations:AcceptSettleReconciler";

    /// <summary>
    /// How often the reconciler looks for accepted requests whose conversation
    /// chat-service does not agree is settled.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How far back a candidate's <c>created_at</c> may be. Bounds the sweep so it
    /// cannot walk the entire request history every tick, and lets a permanently
    /// unsettleable row age out instead of being retried forever.
    ///
    /// <para>Note the deliberate consequence on FIRST BOOT after this ships: every
    /// accepted request inside this window is a candidate, so the first few sweeps also
    /// heal conversations broken by the pre-GW5 two-call sequence. That is wanted, and
    /// it is bounded by <see cref="PageSize"/>.</para>
    /// </summary>
    public TimeSpan LookBack { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Bounded candidate page per sweep (LIMIT). Each candidate costs ONE cheap
    /// membership read against chat-service, and only a genuinely divergent one costs a
    /// settle, so the steady-state cost is <c>PageSize</c> reads per
    /// <see cref="SweepInterval"/> and nothing else.
    /// </summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Master switch. Off disables the sweep entirely (the inline accept-path settle is
    /// unaffected). Present so an operator can stop the sweep without a redeploy if
    /// chat-service is being drained.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
