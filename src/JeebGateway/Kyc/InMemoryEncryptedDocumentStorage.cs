using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace JeebGateway.Kyc;

/// <summary>
/// MVP in-memory implementation of <see cref="IKycDocumentStorage"/>.
/// Wraps each payload in an AES-GCM envelope with a fresh per-object
/// nonce so identical uploads still produce distinct ciphertexts, and
/// retains only the ciphertext + nonce in process memory. Production
/// wiring will swap to S3/MinIO with the same envelope shape.
/// </summary>
public class InMemoryEncryptedDocumentStorage : IKycDocumentStorage
{
    private readonly ConcurrentDictionary<string, EncryptedRecord> _byId = new();
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public InMemoryEncryptedDocumentStorage(TimeProvider clock)
    {
        _clock = clock;
        _key = new byte[32];
        RandomNumberGenerator.Fill(_key);
    }

    public Task<string> PutAsync(
        string ownerId,
        string fileName,
        string contentType,
        byte[] plaintext,
        CancellationToken ct)
    {
        var id = $"kycdoc_{Guid.NewGuid():N}";
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        _byId[id] = new EncryptedRecord
        {
            Id = id,
            OwnerId = ownerId,
            FileName = fileName,
            ContentType = contentType,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Tag = tag,
            SizeBytes = plaintext.LongLength,
            StoredAt = _clock.GetUtcNow()
        };
        return Task.FromResult(id);
    }

    public Task<StoredKycDocument?> GetAsync(string documentId, CancellationToken ct)
    {
        if (!_byId.TryGetValue(documentId, out var record))
        {
            return Task.FromResult<StoredKycDocument?>(null);
        }

        var snapshot = record;
        var keyCopy = _key;
        var doc = new StoredKycDocument
        {
            Id = snapshot.Id,
            OwnerId = snapshot.OwnerId,
            FileName = snapshot.FileName,
            ContentType = snapshot.ContentType,
            SizeBytes = snapshot.SizeBytes,
            StoredAt = snapshot.StoredAt,
            Decrypt = () =>
            {
                var plaintext = new byte[snapshot.Ciphertext.Length];
                using var aes = new AesGcm(keyCopy, snapshot.Tag.Length);
                aes.Decrypt(snapshot.Nonce, snapshot.Ciphertext, snapshot.Tag, plaintext);
                return plaintext;
            }
        };
        return Task.FromResult<StoredKycDocument?>(doc);
    }

    private sealed class EncryptedRecord
    {
        public required string Id { get; init; }
        public required string OwnerId { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public required byte[] Ciphertext { get; init; }
        public required byte[] Nonce { get; init; }
        public required byte[] Tag { get; init; }
        public required long SizeBytes { get; init; }
        public required DateTimeOffset StoredAt { get; init; }
    }
}
