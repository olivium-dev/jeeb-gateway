namespace JeebGateway.Kyc;

/// <summary>
/// Abstraction over encrypted document storage for KYC uploads
/// (T-backend-004). Production wiring will swap in an S3-compatible
/// object store with server-side encryption and a per-object KMS data
/// key; the MVP in-memory implementation applies a deterministic XOR
/// stream against a per-process key so the bytes are never written to
/// disk in plaintext and tests can still round-trip them.
/// </summary>
public interface IKycDocumentStorage
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> at rest and returns the
    /// opaque handle the submission row stores. The handle is unguessable
    /// — leaking the database row alone does not let an attacker decrypt
    /// the document without the encryption key.
    /// </summary>
    Task<string> PutAsync(
        string ownerId,
        string fileName,
        string contentType,
        byte[] plaintext,
        CancellationToken ct);

    Task<StoredKycDocument?> GetAsync(string documentId, CancellationToken ct);
}

public class StoredKycDocument
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset StoredAt { get; init; }

    /// <summary>
    /// Returns the decrypted bytes. The handler is read-only by design —
    /// callers cannot mutate the stored payload, only fetch a fresh
    /// plaintext copy each time.
    /// </summary>
    public required Func<byte[]> Decrypt { get; init; }
}
