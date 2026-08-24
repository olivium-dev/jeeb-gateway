using System.Diagnostics;
using System.Text.Json.Serialization;
using JeebGateway.Conversations.Realtime;
using JeebGateway.Realtime;
using Microsoft.Extensions.Options;

namespace JeebGateway.Operations.RealtimeProbe;

internal enum RealtimeProbeMintStatus
{
    Success,
    Malformed,
    Stale,
    Forbidden,
    Replay,
    Unavailable,
}

internal sealed record RealtimeProbeMintResult(
    RealtimeProbeMintStatus Status,
    RealtimeProbeDescriptor? Descriptor = null);

/// <summary>The exact JSON document consumed by the infrastructure edge probe.</summary>
public sealed record RealtimeProbeDescriptor
{
    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("topic")]
    public required string Topic { get; init; }

    [JsonPropertyName("roleInConvo")]
    public required string RoleInConvo { get; init; }

    [JsonPropertyName("socketUrl")]
    public required string SocketUrl { get; init; }

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("ticket")]
    public required string Ticket { get; init; }

    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }
}

internal interface IRealtimeProbeDescriptorService
{
    Task<RealtimeProbeMintResult> MintAsync(
        IHeaderDictionary headers,
        CancellationToken cancellationToken);
}

internal sealed record RealtimeProbeCredentials(
    RealtimeGuardianToken Guardian,
    string Ticket);

internal interface IRealtimeProbeCredentialIssuer
{
    RealtimeProbeCredentials? Issue(
        string conversationId,
        string viewerId,
        string role,
        string topic);
}

/// <summary>
/// Resolves the two existing signing authorities only inside the endpoint's
/// guarded mint operation. A missing/unreadable mounted key is therefore mapped
/// to the contract's 503 instead of escaping during parameter activation as 500.
/// </summary>
internal sealed class RealtimeProbeCredentialIssuer : IRealtimeProbeCredentialIssuer
{
    private readonly IServiceProvider _services;

    public RealtimeProbeCredentialIssuer(IServiceProvider services)
    {
        _services = services;
    }

    public RealtimeProbeCredentials? Issue(
        string conversationId,
        string viewerId,
        string role,
        string topic)
    {
        var guardian = _services.GetRequiredService<IRealtimeGuardianTokenIssuer>();
        var tickets = _services.GetRequiredService<IRealtimeTicketIssuer>();
        if (!guardian.IsConfigured)
        {
            return null;
        }

        var token = guardian.Issue(
            viewerId,
            topic,
            RealtimeGuardianTokenIssuer.SubscribeOnly,
            RealtimeProbeDescriptorService.CredentialLifetime);
        if (token is null)
        {
            return null;
        }

        return new RealtimeProbeCredentials(
            token,
            tickets.Issue(conversationId, viewerId, role));
    }
}

internal static class RealtimeProbeTelemetry
{
    internal const string ActivitySourceName = "Jeeb.Gateway.Operations.RealtimeProbe";
    internal static readonly ActivitySource Activities = new(ActivitySourceName);
}

/// <summary>
/// Mints a synthetic, non-privileged descriptor. It reads/writes no customer or
/// conversation data and reuses the same Guardian/ticket issuers as the mobile path.
/// </summary>
internal sealed class RealtimeProbeDescriptorService : IRealtimeProbeDescriptorService
{
    internal const string ExactPublicSocketUrl = "wss://app.jeeb.fds-1.com/socket/websocket";
    internal const string ExactGuardianIssuer = "live_comm";
    internal static readonly TimeSpan CredentialLifetime = TimeSpan.FromSeconds(120);

    private readonly IRealtimeProbeRequestAuthenticator _authenticator;
    private readonly IRealtimeProbeReplayStore _replay;
    private readonly IRealtimeProbeCredentialIssuer _credentials;
    private readonly IRealtimeProbeCredentialConfigurationGuard _credentialConfiguration;
    private readonly RealtimeTopicNames _topics;
    private readonly RealtimeGuardianOptions _realtime;
    private readonly ILogger<RealtimeProbeDescriptorService> _logger;

    public RealtimeProbeDescriptorService(
        IRealtimeProbeRequestAuthenticator authenticator,
        IRealtimeProbeReplayStore replay,
        IRealtimeProbeCredentialIssuer credentials,
        IRealtimeProbeCredentialConfigurationGuard credentialConfiguration,
        RealtimeTopicNames topics,
        IOptions<RealtimeGuardianOptions> realtime,
        ILogger<RealtimeProbeDescriptorService> logger)
    {
        _authenticator = authenticator;
        _replay = replay;
        _credentials = credentials;
        _credentialConfiguration = credentialConfiguration;
        _topics = topics;
        _realtime = realtime.Value;
        _logger = logger;
    }

    public async Task<RealtimeProbeMintResult> MintAsync(
        IHeaderDictionary headers,
        CancellationToken cancellationToken)
    {
        using var activity = RealtimeProbeTelemetry.Activities.StartActivity(
            "staging.realtime.probe_descriptor.mint",
            ActivityKind.Internal);

        var authentication = _authenticator.Authenticate(headers);
        var rejected = AuthenticationFailure(authentication.Status);
        if (rejected is not null)
        {
            SetOutcome(activity, rejected.Status);
            return rejected;
        }

        var nonce = authentication.Nonce!;
        var conversationId = "edge-probe-" + nonce;
        var topic = _topics.ChatChannelFor(conversationId);
        if (!_credentialConfiguration.IsExact
            || topic is null
            || !string.Equals(topic, "jeeb:chat:" + conversationId, StringComparison.Ordinal)
            || !string.Equals(
                _realtime.PublicSocketUrl,
                ExactPublicSocketUrl,
                StringComparison.Ordinal)
            || !string.Equals(
                _realtime.GuardianIssuer,
                ExactGuardianIssuer,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Staging realtime probe descriptor dependencies are incomplete; failing closed.");
            SetOutcome(activity, RealtimeProbeMintStatus.Unavailable);
            return new(RealtimeProbeMintStatus.Unavailable);
        }

        var reservation = await _replay.TryReserveAsync(nonce, cancellationToken);
        if (reservation == RealtimeProbeReplayReservation.Replay)
        {
            _logger.LogWarning("Staging realtime probe replay was rejected.");
            SetOutcome(activity, RealtimeProbeMintStatus.Replay);
            return new(RealtimeProbeMintStatus.Replay);
        }

        if (reservation != RealtimeProbeReplayReservation.Acquired)
        {
            SetOutcome(activity, RealtimeProbeMintStatus.Unavailable);
            return new(RealtimeProbeMintStatus.Unavailable);
        }

        try
        {
            const string role = "client";
            var viewerId = conversationId;
            var credentials = _credentials.Issue(
                conversationId,
                viewerId,
                role,
                topic);

            if (credentials is null
                || string.IsNullOrWhiteSpace(credentials.Guardian.Token)
                || string.IsNullOrWhiteSpace(credentials.Ticket)
                || credentials.Guardian.ExpiresAt == default)
            {
                _logger.LogWarning(
                    "Staging realtime probe credential issuer returned an incomplete result.");
                SetOutcome(activity, RealtimeProbeMintStatus.Unavailable);
                return new(RealtimeProbeMintStatus.Unavailable);
            }

            var descriptor = new RealtimeProbeDescriptor
            {
                ConversationId = conversationId,
                Topic = topic,
                RoleInConvo = role,
                SocketUrl = ExactPublicSocketUrl,
                Token = credentials.Guardian.Token,
                Ticket = credentials.Ticket,
                ExpiresAt = credentials.Guardian.ExpiresAt,
            };

            _logger.LogInformation("Staging realtime probe descriptor minted successfully.");
            SetOutcome(activity, RealtimeProbeMintStatus.Success);
            return new(RealtimeProbeMintStatus.Success, descriptor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // No token, ticket, nonce, or header value is included in logs.
            _logger.LogWarning(
                "Staging realtime probe credential mint failed closed ({FailureType}).",
                exception.GetType().Name);
            SetOutcome(activity, RealtimeProbeMintStatus.Unavailable);
            return new(RealtimeProbeMintStatus.Unavailable);
        }
    }

    private static RealtimeProbeMintResult? AuthenticationFailure(
        RealtimeProbeAuthenticationStatus status) => status switch
        {
            RealtimeProbeAuthenticationStatus.Authenticated => null,
            RealtimeProbeAuthenticationStatus.Malformed => new(RealtimeProbeMintStatus.Malformed),
            RealtimeProbeAuthenticationStatus.Stale => new(RealtimeProbeMintStatus.Stale),
            RealtimeProbeAuthenticationStatus.Forbidden => new(RealtimeProbeMintStatus.Forbidden),
            _ => new(RealtimeProbeMintStatus.Unavailable),
        };

    private static void SetOutcome(Activity? activity, RealtimeProbeMintStatus status)
    {
        activity?.SetTag("jeeb.realtime_probe.outcome", status.ToString().ToLowerInvariant());
        activity?.SetStatus(
            status == RealtimeProbeMintStatus.Success
                ? ActivityStatusCode.Ok
                : ActivityStatusCode.Error);
    }
}
