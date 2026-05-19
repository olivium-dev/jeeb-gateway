using JeebGateway.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Ratings;

/// <summary>
/// T-BE-025 / JEB-61 — options for the daily mutual-blind auto-reveal cron.
///
/// Spec calls for a daily run at <c>03:00 Asia/Beirut</c>. We compute the
/// next 03:00 Beirut by converting the current UTC clock to Beirut local
/// time, advancing to the next 03:00 boundary, then converting back to
/// UTC for the sleep. The IANA "Asia/Beirut" zone is honoured so DST
/// transitions (CEST↔CET) are handled by the BCL.
///
/// <para><see cref="RatingWindow"/> defaults to 7 days to match the JEB-61
/// requirement; <see cref="MaxBatchSize"/> caps a single cron tick so a
/// burst of stale rows doesn't open a long-running transaction in
/// production.</para>
/// </summary>
public sealed class MutualBlindAutoRevealOptions
{
    public const string SectionName = "MutualBlindAutoReveal";

    /// <summary>How long after delivery a row remains eligible for blind
    /// submission. Spec: 7 days.</summary>
    public TimeSpan RatingWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Wall-clock time the cron fires every day, in the
    /// <see cref="ScheduleTimeZoneId"/> zone. Spec: 03:00 Asia/Beirut.</summary>
    public TimeSpan ScheduleLocalTimeOfDay { get; set; } = new(03, 00, 00);

    /// <summary>IANA time-zone the cron is anchored to. The BCL accepts
    /// IANA ids on .NET 8 across Linux/macOS/Windows via ICU.</summary>
    public string ScheduleTimeZoneId { get; set; } = "Asia/Beirut";

    /// <summary>Maximum rows processed in a single cron tick. A protective
    /// cap so a backlog can't open an unbounded transaction; the next tick
    /// picks up the remainder.</summary>
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// Test-only override: when set, the cron sleeps for this duration
    /// between ticks instead of computing the next 03:00 Beirut boundary.
    /// Production wiring leaves this null so the real schedule applies.
    /// </summary>
    public TimeSpan? OverrideTickInterval { get; set; }

    /// <summary>
    /// Test-only override: when true, the cron runs once at startup
    /// before sleeping. Production wiring leaves this false so the daily
    /// 03:00 anchor is honoured.
    /// </summary>
    public bool RunImmediatelyOnStart { get; set; }
}

/// <summary>
/// T-BE-025 / JEB-61 — daily auto-reveal cron for stale mutual-blind ratings.
///
/// Once a day at 03:00 Asia/Beirut the job:
/// <list type="number">
///   <item>Snapshots every <see cref="RatingPair"/> whose
///         <c>delivered_at + 7d &lt;= now</c>, where the row is not already
///         auto-revealed and where at most one side submitted a rating
///         (rows where both sides submitted have already revealed
///         themselves and are skipped).</item>
///   <item>For each row, atomically stamps
///         <see cref="RatingPair.AutoRevealedAt"/>. The stamp is the
///         idempotency key — a second cron pass over the same row finds it
///         already stamped, skips the notification, and does not increment
///         <c>revealedCount</c> (AC2 — no double reveal).</item>
///   <item>Fires the <see cref="NotificationTrigger.RatingAutoRevealed"/>
///         push to every party who DID NOT submit a rating (the "rated
///         party" in the spec's language — the one whose review window
///         lapsed). Spec line: "Notify the rated party via
///         notification-service template <c>rating_auto_revealed</c>".
///         When NEITHER side submitted, both parties are notified.</item>
///   <item>Logs <c>revealedCount</c> as a structured field for AC3.</item>
/// </list>
///
/// The job NEVER writes a synthetic rating — the missing side stays null
/// in the row and in the projected <see cref="BlindRevealView"/>, matching
/// the JEB-61 system-design line "do NOT auto-fill any score".
///
/// Production swap: <see cref="IRatingStore.ListPendingAutoRevealAsync"/>
/// and <see cref="IRatingStore.TryMarkAutoRevealedAsync"/> become
/// parameterised SQL against Postgres; the push fan-out stays unchanged.
/// </summary>
public sealed class MutualBlindAutoRevealJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<MutualBlindAutoRevealOptions> _opts;
    private readonly ILogger<MutualBlindAutoRevealJob> _log;

    public MutualBlindAutoRevealJob(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IOptionsMonitor<MutualBlindAutoRevealOptions> opts,
        ILogger<MutualBlindAutoRevealJob> log)
    {
        _scopes = scopes;
        _clock = clock;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var firstTick = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _opts.CurrentValue;
            if (!(firstTick && opts.RunImmediatelyOnStart))
            {
                var delay = ComputeNextDelay(opts);
                try
                {
                    await Task.Delay(delay, _clock, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }

            firstTick = false;

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // We log and continue — a single tick failure must not crash
                // the daily schedule. The next tick will pick up the same
                // rows because they remain unstamped.
                _log.LogError(ex, "auto_reveal_ratings: cron tick failed");
            }
        }
    }

    /// <summary>
    /// Public for test/operator use. Production callers should let the
    /// background loop drive this; tests invoke it directly to bypass
    /// the schedule sleep.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var opts = _opts.CurrentValue;
        await using var scope = _scopes.CreateAsyncScope();
        var ratings = scope.ServiceProvider.GetRequiredService<IRatingStore>();
        var push = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var now = _clock.GetUtcNow();
        var snapshot = await ratings.ListPendingAutoRevealAsync(now, opts.RatingWindow, ct);

        var revealedCount = 0;
        var notifiedCount = 0;
        var failedNotifications = 0;

        foreach (var pair in snapshot.Take(opts.MaxBatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var stamped = await ratings.TryMarkAutoRevealedAsync(pair.DeliveryId, now, ct);
            if (!stamped)
            {
                // AC2 — another worker (or a previous cron pass) already
                // stamped this row. Do NOT count it and do NOT re-notify.
                continue;
            }

            revealedCount++;

            foreach (var recipient in ResolveAutoRevealRecipients(pair))
            {
                try
                {
                    await push.SendAsync(new PushNotificationRequest(
                        UserId: recipient,
                        Trigger: NotificationTrigger.RatingAutoRevealed,
                        Title: "Your rating has been revealed",
                        Body: "The 7-day rating window closed without both sides rating. Your delivery has been auto-revealed.",
                        Data: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["deliveryId"] = pair.DeliveryId,
                            ["template"] = "rating_auto_revealed",
                            ["autoRevealedAt"] = now.ToString("O"),
                        },
                        IdempotencyKey: $"rating-auto-reveal:{pair.DeliveryId}:{recipient}"),
                        ct);
                    notifiedCount++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Push failure must NOT roll back the reveal stamp —
                    // the row is authoritatively revealed; the push is
                    // best-effort. The PushNotificationService already
                    // owns its own retry-once policy (T-backend-022).
                    failedNotifications++;
                    _log.LogWarning(ex,
                        "auto_reveal_ratings: push failed for delivery {DeliveryId} recipient {Recipient}",
                        pair.DeliveryId, recipient);
                }
            }
        }

        // AC3 — emit revealedCount as a structured field. We always log so
        // operators can see "0 ratings revealed" runs as a heartbeat; in
        // production this becomes a `auto_reveal_ratings.revealed_count`
        // metric via the OpenTelemetry pipeline.
        _log.LogInformation(
            "auto_reveal_ratings completed: revealedCount={RevealedCount} notifiedCount={NotifiedCount} failedNotifications={FailedNotifications} scanned={ScannedCount}",
            revealedCount, notifiedCount, failedNotifications, snapshot.Count);

        return revealedCount;
    }

    /// <summary>
    /// JEB-61 spec: "Notify the rated party via notification-service
    /// template <c>rating_auto_revealed</c>." The rated party is the side
    /// whose review window lapsed — i.e. the side that DID NOT submit.
    /// When neither side submitted, both are notified (both lost the
    /// window). When one side submitted (rare for auto-reveal — typically
    /// indicates the other half never came in), the non-submitting side
    /// is notified.
    /// </summary>
    private static IEnumerable<string> ResolveAutoRevealRecipients(RatingPair pair)
    {
        if (pair.ClientRating is null)
            yield return pair.ClientId;
        if (pair.JeeberRating is null)
            yield return pair.JeeberId;
    }

    /// <summary>
    /// Computes the next absolute fire instant minus the current UTC time,
    /// honouring <see cref="MutualBlindAutoRevealOptions.OverrideTickInterval"/>
    /// for test wiring. A floor of 1 second guards against pathological
    /// cases (e.g. the clock just crossed the scheduled time) so the loop
    /// can't spin.
    /// </summary>
    internal TimeSpan ComputeNextDelay(MutualBlindAutoRevealOptions opts)
    {
        if (opts.OverrideTickInterval is { } overrideInterval && overrideInterval > TimeSpan.Zero)
            return overrideInterval;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(opts.ScheduleTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            _log.LogWarning(
                "auto_reveal_ratings: time zone {Tz} not found, falling back to UTC",
                opts.ScheduleTimeZoneId);
            tz = TimeZoneInfo.Utc;
        }

        var nowUtc = _clock.GetUtcNow();
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);

        var todayLocalFire = new DateTimeOffset(
            nowLocal.Year, nowLocal.Month, nowLocal.Day,
            opts.ScheduleLocalTimeOfDay.Hours,
            opts.ScheduleLocalTimeOfDay.Minutes,
            opts.ScheduleLocalTimeOfDay.Seconds,
            nowLocal.Offset);

        var nextLocalFire = todayLocalFire > nowLocal
            ? todayLocalFire
            : todayLocalFire.AddDays(1);

        // Re-anchor to the zone's offset on the target date (DST may shift).
        var nextLocalFireZoned = new DateTimeOffset(
            nextLocalFire.DateTime,
            tz.GetUtcOffset(nextLocalFire.DateTime));

        var delay = nextLocalFireZoned - nowUtc;
        return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
    }
}
