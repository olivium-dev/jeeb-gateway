using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace JeebGateway.Realtime;

/// <summary>The one place LiveComm topic/channel names are built, all derived from
/// <c>Services:Realtime:TenantPrefix</c> (default preserves today's live names).</summary>
public sealed class RealtimeTopicNames
{
    // Same alphabet CourierPositionTopic enforces on ids: ':' or '*' in the prefix
    // would widen or escape the realtime ACL namespace, so refuse at boot.
    private static readonly Regex SafePrefix = new(
        "^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RealtimeTopicNames(IOptions<RealtimeGuardianOptions> options)
    {
        var prefix = options.Value.TenantPrefix;
        if (string.IsNullOrWhiteSpace(prefix) || !SafePrefix.IsMatch(prefix))
        {
            throw new InvalidOperationException(
                $"{RealtimeGuardianOptions.SectionName}:TenantPrefix must be a plain "
                + "[A-Za-z0-9_-] token; ':' or '*' would escape the realtime ACL namespace.");
        }

        TenantPrefix = prefix;
        ChatTopic = prefix + ":chat";
        DeliveryTopicPrefix = prefix + ":delivery:";
        ConversationChannelPrefix = prefix + "_conversation:";
    }

    /// <summary>The configured tenant segment (default the legacy literal).</summary>
    public string TenantPrefix { get; }

    /// <summary>The 1:1 chat fan-out ingest topic, <c>{prefix}:chat</c>.</summary>
    public string ChatTopic { get; }

    /// <summary>Courier-position topic prefix, <c>{prefix}:delivery:</c>.</summary>
    public string DeliveryTopicPrefix { get; }

    /// <summary>Phoenix chat channel prefix, <c>{prefix}_conversation:</c>.</summary>
    public string ConversationChannelPrefix { get; }

    /// <summary>The position topic for a delivery, or null when the id is not a safe
    /// segment (see <see cref="CourierPositionTopic"/> on why unsafe ids are refused).</summary>
    public string? DeliveryTopicFor(string? deliveryId)
        => CourierPositionTopic.IsSafeDeliveryId(deliveryId)
            ? DeliveryTopicPrefix + deliveryId
            : null;

    /// <summary>The Phoenix channel a chat client joins for a conversation.</summary>
    public string ConversationChannelFor(string conversationId)
        => ConversationChannelPrefix + conversationId;

    /// <summary>Route-tenant gate: the configured prefix plus the default as a
    /// deprecated alias, so pre-rename URLs keep working after a config flip.</summary>
    public bool IsAcceptedTenant(string? tenant)
        => string.Equals(tenant, TenantPrefix, StringComparison.Ordinal)
           || string.Equals(tenant, RealtimeGuardianOptions.DefaultTenantPrefix,
                StringComparison.Ordinal);
}
