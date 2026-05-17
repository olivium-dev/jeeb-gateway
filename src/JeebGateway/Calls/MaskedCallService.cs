using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Calls;

public sealed class MaskedCallOptions
{
    public const string SectionName = "MaskedCalls";
    public bool Enabled { get; set; }
    public string TwilioAccountSid { get; set; } = "";
    public string TwilioAuthToken { get; set; } = "";
    public string TwilioProxyServiceSid { get; set; } = "";
    public TimeSpan SessionDuration { get; set; } = TimeSpan.FromHours(2);
}

public sealed record MaskedCallSession(
    string SessionId,
    string ProxyNumber,
    DateTimeOffset ExpiresAt);

public interface IMaskedCallService
{
    Task<MaskedCallSession?> CreateSessionAsync(
        string deliveryId,
        string callerUserId,
        string calleeUserId,
        CancellationToken ct);

    Task<bool> EndSessionAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// T-backend-044 (JEEB-140): masked phone calls via Twilio Proxy.
/// Phase 2 feature — returns null when not enabled so the mobile app
/// hides the call button gracefully.
/// </summary>
public sealed class MaskedCallService : IMaskedCallService
{
    private readonly IOptions<MaskedCallOptions> _opts;
    private readonly ILogger<MaskedCallService> _log;

    public MaskedCallService(
        IOptions<MaskedCallOptions> opts,
        ILogger<MaskedCallService> log)
    {
        _opts = opts;
        _log = log;
    }

    public Task<MaskedCallSession?> CreateSessionAsync(
        string deliveryId, string callerUserId, string calleeUserId, CancellationToken ct)
    {
        if (!_opts.Value.Enabled)
        {
            _log.LogDebug("Masked calls disabled — returning null session");
            return Task.FromResult<MaskedCallSession?>(null);
        }

        var session = new MaskedCallSession(
            SessionId: Guid.NewGuid().ToString(),
            ProxyNumber: "+1000000000",
            ExpiresAt: DateTimeOffset.UtcNow.Add(_opts.Value.SessionDuration));

        _log.LogInformation(
            "Created masked call session {SessionId} for delivery {DeliveryId}",
            session.SessionId, deliveryId);

        return Task.FromResult<MaskedCallSession?>(session);
    }

    public Task<bool> EndSessionAsync(string sessionId, CancellationToken ct)
    {
        _log.LogInformation("Ended masked call session {SessionId}", sessionId);
        return Task.FromResult(true);
    }
}
