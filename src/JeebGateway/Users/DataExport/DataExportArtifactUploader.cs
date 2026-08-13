using System.Net.Http.Headers;
using System.Security.Cryptography;
using JeebGateway.Services.Cdn;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users.DataExport;

// gwdbx W1-06 (G-20) — encrypt-then-upload. The packaged export reaches cdn as ciphertext
// under a random objectRef; cdn-service is an owner-protected shared AWS service (call only).
public interface IDataExportArtifactUploader
{
    Task<DataExportArtifact> UploadAsync(byte[] plaintext, CancellationToken ct);
}

public sealed record DataExportArtifact(string ObjectRef, long CiphertextBytes);

public sealed class CdnDataExportArtifactUploader : IDataExportArtifactUploader
{
    public const string ArtifactContentType = "application/octet-stream";

    // cdn mints {slot}/{guid:N}{extension}; the gateway contributes its own 128 random bits
    // through the extension, so the name is unguessable even if one source were weakened.
    public const int NonceBytes = 16;
    public const int MinObjectRefEntropyHexChars = 32;

    // Required by CdnUploadUrlRequest but never serialized by CDNServiceClient. G-20 keeps
    // user identity out of every cdn request, so the service identity goes here.
    private const string OwnerRef = "jeeb-gateway";

    private readonly ICDNServiceClient _cdn;
    private readonly IHttpClientFactory _httpFactory;
    private readonly DataExportArtifactCipher _cipher;
    private readonly IOptions<DataExportArtifactOptions> _options;

    public CdnDataExportArtifactUploader(
        ICDNServiceClient cdn,
        IHttpClientFactory httpFactory,
        DataExportArtifactCipher cipher,
        IOptions<DataExportArtifactOptions> options)
    {
        _cdn = cdn;
        _httpFactory = httpFactory;
        _cipher = cipher;
        _options = options;
    }

    public async Task<DataExportArtifact> UploadAsync(byte[] plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        // G-20 — encryption happens FIRST and unconditionally: no branch below this line
        // can reach cdn with plaintext PII.
        var ciphertext = _cipher.Encrypt(plaintext);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes)).ToLowerInvariant();

        var ticket = await _cdn.MintUploadUrlAsync(
            new CdnUploadUrlRequest
            {
                Slot = _options.Value.CdnSlot,
                ContentType = ArtifactContentType,
                OwnerUserId = OwnerRef,
                TtlSeconds = _options.Value.UploadTtlSeconds,
                Extension = "." + nonce + ".bin",
            },
            ct);

        var objectRef = ticket.ObjectRef?.Trim() ?? string.Empty;
        if (!IsUnguessable(objectRef))
        {
            throw new InvalidOperationException(
                "cdn minted a data-export objectRef without at least 128 bits of randomness; " +
                "refusing to upload an export artifact to a guessable name (G-20).");
        }

        await PutAsync(ticket, ciphertext, ct);
        return new DataExportArtifact(objectRef, ciphertext.LongLength);
    }

    // The random component must survive in the minted name: >= 32 hex chars (128 bits) in
    // the final segment. cdn's own {guid:N} satisfies this even if the extension is ignored.
    public static bool IsUnguessable(string objectRef)
    {
        if (string.IsNullOrWhiteSpace(objectRef))
        {
            return false;
        }

        var name = objectRef[(objectRef.LastIndexOf('/') + 1)..];
        var run = 0;
        var longest = 0;
        foreach (var c in name)
        {
            run = Uri.IsHexDigit(c) ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest >= MinObjectRefEntropyHexChars;
    }

    // SSRF guard: cdn's Local provider returns a relative signed-PUT path (dialed against the
    // pinned cdn base); an absolute target is accepted only over https or on cdn's own origin.
    public static bool IsAcceptableUploadTarget(string uploadUrl, Uri? cdnBase)
    {
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var abs) || string.IsNullOrEmpty(abs.Host))
        {
            return cdnBase is not null;
        }
        if (abs.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }
        return abs.Scheme == Uri.UriSchemeHttp
            && cdnBase is not null
            && string.Equals(abs.Host, cdnBase.Host, StringComparison.OrdinalIgnoreCase)
            && abs.Port == cdnBase.Port;
    }

    private async Task PutAsync(CdnUploadTicket ticket, byte[] ciphertext, CancellationToken ct)
    {
        // Signed-PUT client: no bearer (the HMAC/S3 signature IS the credential), no retry
        // handler, no redirect chasing (CdnUploadUrlResolver.ProxyHttpClientName).
        var http = _httpFactory.CreateClient(CdnUploadUrlResolver.ProxyHttpClientName);
        if (!IsAcceptableUploadTarget(ticket.UploadUrl, http.BaseAddress))
        {
            throw new InvalidOperationException(
                $"cdn returned an unacceptable signed-PUT target for a data-export artifact.");
        }

        var method = string.IsNullOrWhiteSpace(ticket.Method) ? "PUT" : ticket.Method;
        using var request = new HttpRequestMessage(new HttpMethod(method), ticket.UploadUrl);
        var content = new ByteArrayContent(ciphertext);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ArtifactContentType);
        foreach (var header in ticket.RequiredHeaders)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                continue;
            }
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = content;

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
