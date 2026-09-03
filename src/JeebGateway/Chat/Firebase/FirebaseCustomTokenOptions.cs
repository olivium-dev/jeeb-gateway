namespace JeebGateway.Chat.Firebase;

/// <summary>
/// Configuration for the Firebase custom-token mint route
/// (<c>POST /v1/chat/firebase-token</c>).
///
/// <para>The service-account key is referenced by ABSOLUTE PATH ON THE HOST and is
/// never carried in this repository. There is deliberately no embedded default, no
/// inline-JSON option, and no fallback: if <see cref="ServiceAccountKeyPath"/> is not
/// configured the local-development route reports itself unavailable rather than
/// minting anything. Deployment authorities always mount this path, and startup eagerly
/// validates it before readiness. See <see cref="FirebaseCustomTokenMinter"/> for the
/// guards that make it impossible to read a credential committed to the repo.</para>
/// </summary>
public sealed class FirebaseCustomTokenOptions
{
    public const string SectionName = "Firebase:Chat";

    /// <summary>
    /// Absolute filesystem path to the Firebase service-account JSON on the host.
    /// Empty is allowed only for local development/testing and means the mint route is
    /// switched off. Every deployment workflow requires the protected credential and
    /// sets this to <c>/run/secrets/firebase_admin_json</c>.
    /// </summary>
    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// The Firebase project the minted tokens are for. The key file's own
    /// <c>project_id</c> must match this exactly, or the minter refuses to load it —
    /// so a mis-pointed key fails closed instead of minting cross-tenant identities.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Token lifetime in seconds. Firebase rejects custom tokens with a lifetime over
    /// one hour, so this is clamped to [60, 3600] at mint time.
    ///
    /// <para><b>This bounds the CUSTOM token only — it is not a revocation control.</b>
    /// The custom token is single-use in practice: the client trades it at
    /// <c>signInWithCustomToken</c> for a Firebase session that then refreshes itself
    /// indefinitely, with no further contact with this gateway. Shortening this value
    /// shortens the window in which an intercepted custom token can be REDEEMED; it does
    /// nothing to an already-redeemed session. Access is revoked by conversation
    /// membership alone (<c>RemovedAt</c> on the participant row), enforced by the
    /// Firestore rule.</para>
    /// </summary>
    public int TokenLifetimeSeconds { get; set; } = 3600;
}
