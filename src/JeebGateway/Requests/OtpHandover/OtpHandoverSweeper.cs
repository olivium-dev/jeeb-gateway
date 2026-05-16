using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Background sweeper that escalates deliveries where the Jeeber
/// flagged the Client as unreachable and the
/// <see cref="OtpHandoverOptions.ClientUnreachableWindow"/> has elapsed
/// without a successful OTP verification (T-backend-015 step 6).
///
/// Each pass:
/// <list type="number">
///   <item>Asks the store for rows whose <c>ClientUnreachableAt</c> is at
///     or before <c>now - ClientUnreachableWindow</c> and that do not
///     yet carry an <c>OtpEscalationId</c>.</item>
///   <item>Creates an escalation row with reason
///     <see cref="EscalationReason.ClientUnreachable"/>.</item>
///   <item>Atomically writes the escalation id back to the delivery via
///     <see cref="IRequestsStore.TrySetEscalationIdAsync"/> — if a
///     racing controller call (e.g. OTP lockout) already stamped an
///     escalation id, the sweeper backs off without duplicating.</item>
/// </list>
/// </summary>
public class OtpHandoverSweeper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly IOptions<OtpHandoverOptions> _options;
    private readonly ILogger<OtpHandoverSweeper> _logger;

    public OtpHandoverSweeper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<OtpHandoverOptions> options,
        ILogger<OtpHandoverSweeper> logger)
    {
        _services = services;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.SweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OTP handover unreachable sweep failed");
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Single sweep pass. Exposed publicly so the integration tests can
    /// drive the sweeper deterministically against a fake clock without
    /// waiting on <c>Task.Delay</c>.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var escalations = scope.ServiceProvider.GetRequiredService<IAdminEscalationStore>();
        var opts = _options.Value;

        var now = _clock.GetUtcNow();
        var cutoff = now - opts.ClientUnreachableWindow;

        var due = await store.ListUnreachableAtOrBeforeAsync(cutoff, ct);
        foreach (var req in due)
        {
            // Create the escalation first, then attempt to stamp it on
            // the delivery. If a racing call already populated the
            // escalation id (e.g. OTP just locked out at the same
            // instant), the second arm is a no-op and the escalation row
            // we created is orphaned but harmless (admin queue is the
            // single consumer; the OtpEscalationId on the row is the
            // canonical pointer the mobile app reads).
            var escalation = await escalations.CreateAsync(new AdminEscalation
            {
                Id = Guid.NewGuid().ToString(),
                DeliveryId = req.Id,
                ClientId = req.ClientId,
                JeeberId = req.JeeberId,
                Reason = EscalationReason.ClientUnreachable,
                Status = EscalationStatus.Pending,
                CreatedAt = now,
                OtpAttemptCount = req.OtpAttemptCount
            }, ct);

            var stamped = await store.TrySetEscalationIdAsync(req.Id, escalation.Id, ct);
            if (stamped)
            {
                _logger.LogWarning(
                    "Delivery {DeliveryId} escalated after {WindowMinutes}m client-unreachable — escalation {EscalationId}",
                    req.Id,
                    opts.ClientUnreachableWindow.TotalMinutes,
                    escalation.Id);
            }
        }
    }
}
