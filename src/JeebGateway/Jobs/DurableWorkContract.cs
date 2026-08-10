using System.Security.Cryptography;
using System.Text;

namespace JeebGateway.Jobs;

/// <summary>
/// Gateway-owned meanings layered on state-service's product-neutral work
/// records. These values are opaque to state-service.
/// </summary>
public static class DurableWorkContract
{
    public const string Application = "jeeb-gateway";
    public const string AccountDeletionKind = "account-deletion";
    public const string DataExportKind = "data-export";
    public const string ChatSettlementKind = "chat-settlement";

    public static string SubjectForUser(string userId) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))
            .ToLowerInvariant();

    public static string SubjectForRequest(string requestId) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)))
            .ToLowerInvariant();
}
