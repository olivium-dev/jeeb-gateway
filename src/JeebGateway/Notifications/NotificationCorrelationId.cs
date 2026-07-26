using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Notifications;

/// <summary>
/// Builds the deterministic notification correlation handle used to join a push
/// with its durable notification-service row. This is a handle, not idempotency:
/// the notification service does not deduplicate repeated notification ids
/// (A5, measured on MSI for FM-1).
/// </summary>
public static class NotificationCorrelationId
{
    private const string Domain = "jeeb-notification-v1";

    public static string Create(
        string templateKey,
        string recipientId,
        string entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var canonical = string.Join('|', Domain, templateKey, recipientId, entityId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        hash[6] = (byte)((hash[6] & 0x0f) | 0x40);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);

        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString("D");
    }
}
