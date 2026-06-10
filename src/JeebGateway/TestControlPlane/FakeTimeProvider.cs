namespace JeebGateway.TestControlPlane;

/// <summary>
/// JEB-1502: Thread-safe <see cref="TimeProvider"/> implementation that wraps
/// the ambient (real) provider and adds a controllable UTC offset.
///
/// <para>
/// This is registered as the <em>singleton</em> <see cref="TimeProvider"/> for
/// the entire gateway DI container so that every time-dependent service —
/// <c>RatingRevealJob</c>, <c>RequestExpirySweeper</c>,
/// <c>WeeklySettlementBatch</c>, and any future cron — automatically observes
/// the shifted clock when the test control-plane advances time.
/// </para>
///
/// <para>
/// At zero offset (the default, and the state in production) this provider is
/// behaviourally identical to <see cref="TimeProvider.System"/>: every call
/// delegates to the inner provider. There is no measurable overhead and no
/// observable difference in production.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> <see cref="_offsetTicks"/> is read/written with
/// <see cref="Interlocked"/> operations. Multiple concurrent calls to
/// <see cref="AdvanceBy"/> and <see cref="Reset"/> are safe; the offset is
/// composed additively for advances (an advance of 3 d followed by one of 4 d
/// gives +7 d).
/// </para>
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly TimeProvider _inner;
    private long _offsetTicks = 0;

    /// <param name="inner">
    /// The real ambient provider to delegate to. Normally
    /// <see cref="TimeProvider.System"/>. Injected so test fixtures can also
    /// pass a <see cref="Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider"/>
    /// for deterministic unit tests of the control-plane itself.
    /// </param>
    public FakeTimeProvider(TimeProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Current UTC offset applied on top of the inner provider.</summary>
    public TimeSpan CurrentOffset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>
    /// Atomically add <paramref name="duration"/> to the current offset.
    /// Returns the new effective now after the advance.
    /// </summary>
    public DateTimeOffset AdvanceBy(TimeSpan duration)
    {
        Interlocked.Add(ref _offsetTicks, duration.Ticks);
        return GetUtcNow();
    }

    /// <summary>Clear the offset — restores wall-clock behaviour.</summary>
    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
        => _inner.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    /// <inheritdoc />
    public override long GetTimestamp()
        => _inner.GetTimestamp();

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
}
