using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

// gwdbx W1-06 (G-20) — AES-GCM envelope for export artifacts: version | nonce | tag | ciphertext.
// cdn fetch is effectively unauthenticated, so the bytes that leave the gateway are ciphertext ONLY.
public sealed class DataExportArtifactCipher
{
    public const byte EnvelopeVersion = 1;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    public const int HeaderBytes = 1 + NonceBytes + TagBytes;

    private readonly Lazy<byte[]> _key;

    public DataExportArtifactCipher(IOptions<DataExportArtifactOptions> options) =>
        _key = new Lazy<byte[]>(() => ReadKey(options.Value.ArtifactKey));

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var envelope = new byte[HeaderBytes + plaintext.Length];
        envelope[0] = EnvelopeVersion;
        var nonce = envelope.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key.Value, TagBytes);
        aes.Encrypt(
            nonce,
            plaintext,
            envelope.AsSpan(HeaderBytes),
            envelope.AsSpan(1 + NonceBytes, TagBytes));
        return envelope;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderBytes || envelope[0] != EnvelopeVersion)
        {
            throw new CryptographicException("Data-export artifact envelope is malformed.");
        }

        var plaintext = new byte[envelope.Length - HeaderBytes];
        using var aes = new AesGcm(_key.Value, TagBytes);
        aes.Decrypt(
            envelope.Slice(1, NonceBytes),
            envelope[HeaderBytes..],
            envelope.Slice(1 + NonceBytes, TagBytes),
            plaintext);
        return plaintext;
    }

    // Boot check for the ladder: a configured, well-formed key is mandatory from
    // dual-write-local-read up, because from there the mirror uploads artifacts.
    public static bool IsUsableKey(string? configured)
    {
        try
        {
            ReadKey(configured);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] ReadKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "DataExport:ArtifactKey is not configured; export artifacts cannot be encrypted (G-20).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("DataExport:ArtifactKey must be base64.", ex);
        }

        if (key.Length is not (16 or 24 or 32))
        {
            throw new InvalidOperationException(
                $"DataExport:ArtifactKey must decode to 16, 24 or 32 bytes (got {key.Length}).");
        }
        return key;
    }
}
