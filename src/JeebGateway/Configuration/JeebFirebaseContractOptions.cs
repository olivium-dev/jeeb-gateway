using Microsoft.Extensions.Configuration;

namespace JeebGateway.Configuration;

/// <summary>
/// Immutable, non-secret Jeeb Firebase identity and ownership contract.
/// Deployments may supply the same values explicitly, but no environment may
/// point chat at another project/database or nominate a second push producer.
/// </summary>
public sealed class JeebFirebaseContractOptions
{
    public const string SectionName = "JeebFirebaseContract";
    public const int CanonicalSchemaVersion = 1;
    public const string CanonicalProjectId = "jeeb-5a293";
    public const string CanonicalProjectNumber = "1051234312170";
    public const string CanonicalFirestoreDatabaseId = "(default)";
    public const string CanonicalPushProducer = "notification-service";

    private static readonly string[] LegacyDatabaseKeys =
    [
        "Firestore:DatabaseId",
        "Firebase:FirestoreDatabaseId",
        "Firebase:Chat:FirestoreDatabaseId",
    ];

    public int SchemaVersion { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectNumber { get; init; } = string.Empty;
    public string FirestoreDatabaseId { get; init; } = string.Empty;
    public bool ChatEnabled { get; init; }
    public string PushProducer { get; init; } = string.Empty;

    public static bool IsCanonical(JeebFirebaseContractOptions options) =>
        options.SchemaVersion == CanonicalSchemaVersion
        && string.Equals(options.ProjectId, CanonicalProjectId, StringComparison.Ordinal)
        && string.Equals(options.ProjectNumber, CanonicalProjectNumber, StringComparison.Ordinal)
        && string.Equals(
            options.FirestoreDatabaseId,
            CanonicalFirestoreDatabaseId,
            StringComparison.Ordinal)
        && options.ChatEnabled
        && string.Equals(
            options.PushProducer,
            CanonicalPushProducer,
            StringComparison.Ordinal);

    /// <summary>
    /// Rejects historical database selectors. The gateway itself does not open
    /// Firestore, but accepting a named selector here would let a stale Swarm
    /// overlay disagree with the mobile/chat-service contract without a loud boot
    /// failure. An explicit <c>(default)</c> is harmless during migration.
    /// </summary>
    public static bool HasNoConflictingDatabaseOverride(IConfiguration configuration) =>
        LegacyDatabaseKeys.All(key =>
        {
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value)
                || string.Equals(
                    value.Trim(),
                    CanonicalFirestoreDatabaseId,
                    StringComparison.Ordinal);
        });
}
