using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.Chat.Firebase;

/// <summary>Outcome of a mint attempt.</summary>
public sealed record FirebaseCustomToken(string Token, DateTimeOffset ExpiresAt, string Uid);

/// <summary>Raised when the mint route is asked to work but cannot be trusted to.</summary>
public sealed class FirebaseCustomTokenUnavailableException : Exception
{
    public FirebaseCustomTokenUnavailableException(string message) : base(message) { }
}

public interface IFirebaseCustomTokenMinter
{
    /// <summary>True when a usable service-account key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Mint a Firebase custom token whose <c>uid</c> is <paramref name="uid"/>.
    /// </summary>
    FirebaseCustomToken Mint(string uid);
}

/// <summary>
/// Mints Firebase CUSTOM tokens (RS256 JWTs signed by the Firebase service account)
/// so the mobile client can call <c>signInWithCustomToken</c> and read its own chat
/// thread straight from Firestore, under the membership rules deployed to the
/// <c>jeeb-5a293</c> project.
///
/// <para><b>The uid is the Jeeb user id.</b> The caller supplies it from
/// <c>UserIdentity.TryGetUserId</c> — the SAME resolver every other chat endpoint on
/// this gateway uses to tell chat-service who the viewer is. That is what makes the
/// Firestore rule work: chat-service stamps that identical value into
/// <c>Conversation.Participants[].UserId</c>, and the rule matches
/// <c>request.auth.uid</c> against it. Note this is deliberately NOT the raw <c>sub</c>
/// claim: user-management-reissued tokens follow the org convention of
/// <c>sub = user.Email</c> and carry the GUID in <c>sid</c>, so minting on <c>sub</c>
/// would both deny every UM-issued session and publish users' e-mail addresses as
/// Firebase uids.</para>
///
/// <para><b>Credential locality.</b> The signing key is loaded from an absolute host
/// path given in configuration. Three guards make it impossible for this route to
/// operate on a credential that shipped with the code: the path must be configured
/// (no default), it must be rooted (no repo-relative paths), and it must resolve
/// OUTSIDE the application's content root. A fourth guard — project pinning — makes a
/// mis-pointed key fail closed rather than mint identities for another tenant.</para>
/// </summary>
public sealed class FirebaseCustomTokenMinter : IFirebaseCustomTokenMinter
{
    /// <summary>The fixed audience every Firebase custom token must carry.</summary>
    internal const string FirebaseAudience =
        "https://identitytoolkit.googleapis.com/google.identity.identitytoolkit.v1.IdentityToolkit";

    private const int MinLifetimeSeconds = 60;
    private const int MaxLifetimeSeconds = 3600; // Firebase hard ceiling.

    private readonly FirebaseCustomTokenOptions _options;
    private readonly ILogger<FirebaseCustomTokenMinter> _logger;
    private readonly string _contentRoot;
    private readonly object _gate = new();

    private ServiceAccountKey? _key;
    private RSAParameters _rsaParameters;
    private bool _loadAttempted;
    private string? _loadFailure;

    public FirebaseCustomTokenMinter(
        IOptions<FirebaseCustomTokenOptions> options,
        IHostEnvironment environment,
        ILogger<FirebaseCustomTokenMinter> logger)
    {
        _options = options.Value;
        _logger = logger;
        _contentRoot = environment.ContentRootPath;
    }

    public bool IsConfigured
    {
        get
        {
            try
            {
                EnsureLoaded();
                return true;
            }
            catch (FirebaseCustomTokenUnavailableException)
            {
                return false;
            }
        }
    }

    public FirebaseCustomToken Mint(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            throw new ArgumentException("uid must be a non-empty Jeeb user id.", nameof(uid));
        }

        // Firebase rejects uids longer than 128 bytes; fail loudly rather than mint a
        // token the client can never exchange.
        if (System.Text.Encoding.UTF8.GetByteCount(uid) > 128)
        {
            throw new ArgumentException("uid exceeds the 128-byte Firebase limit.", nameof(uid));
        }

        EnsureLoaded();
        var key = _key!;

        var lifetime = Math.Clamp(_options.TokenLifetimeSeconds, MinLifetimeSeconds, MaxLifetimeSeconds);
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddSeconds(lifetime);

        // A fresh RSA per mint: RSA instances are not documented as thread-safe for
        // signing, and signature-provider caching would outlive the disposed handle.
        using var rsa = RSA.Create();
        rsa.ImportParameters(_rsaParameters);

        var securityKey = new RsaSecurityKey(rsa)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };

        var token = new JwtSecurityToken(
            issuer: key.ClientEmail,
            audience: FirebaseAudience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, key.ClientEmail),
                new Claim(JwtRegisteredClaimNames.Iat,
                    issuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new Claim("uid", uid),
            },
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256));

        var serialized = new JwtSecurityTokenHandler().WriteToken(token);
        return new FirebaseCustomToken(serialized, expiresAt, uid);
    }

    // -------------------------------------------------------------------------
    // Key loading + the guards that keep a committed credential unusable.
    // -------------------------------------------------------------------------

    private void EnsureLoaded()
    {
        if (_loadAttempted)
        {
            if (_loadFailure is not null)
            {
                throw new FirebaseCustomTokenUnavailableException(_loadFailure);
            }
            return;
        }

        lock (_gate)
        {
            if (_loadAttempted)
            {
                if (_loadFailure is not null)
                {
                    throw new FirebaseCustomTokenUnavailableException(_loadFailure);
                }
                return;
            }

            try
            {
                Load();
                _loadFailure = null;
            }
            catch (FirebaseCustomTokenUnavailableException ex)
            {
                _loadFailure = ex.Message;
                // The path is operator-supplied configuration, not a secret; the key's
                // CONTENTS are never logged.
                _logger.LogWarning(
                    "Firebase chat custom-token minting is unavailable: {Reason}", ex.Message);
                throw;
            }
            finally
            {
                _loadAttempted = true;
            }
        }
    }

    private void Load()
    {
        var path = _options.ServiceAccountKeyPath?.Trim() ?? string.Empty;

        // GUARD 1 — no default, no fallback. Unconfigured means off, not "guess".
        if (string.IsNullOrEmpty(path))
        {
            throw new FirebaseCustomTokenUnavailableException(
                $"{FirebaseCustomTokenOptions.SectionName}:ServiceAccountKeyPath is not configured.");
        }

        // GUARD 2 — absolute paths only. A relative path would be resolved against the
        // application directory, which is exactly where a committed key would land.
        if (!Path.IsPathRooted(path))
        {
            throw new FirebaseCustomTokenUnavailableException(
                "ServiceAccountKeyPath must be an absolute host path.");
        }

        var fullPath = Path.GetFullPath(path);

        // GUARD 3 — the key must live OUTSIDE the deployed application tree. This is
        // what makes it impossible for this route to work by reading a credential that
        // was committed to the repository and published alongside the binaries.
        var contentRoot = Path.GetFullPath(_contentRoot);
        if (!contentRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            contentRoot += Path.DirectorySeparatorChar;
        }

        if (fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new FirebaseCustomTokenUnavailableException(
                "ServiceAccountKeyPath resolves inside the application content root. "
                + "The signing key must be host-local and must never ship with the app.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FirebaseCustomTokenUnavailableException(
                "The configured service-account key file does not exist on this host.");
        }

        ServiceAccountKey? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServiceAccountKey>(File.ReadAllText(fullPath));
        }
        catch (JsonException)
        {
            throw new FirebaseCustomTokenUnavailableException(
                "The configured service-account key file is not valid JSON.");
        }

        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.ClientEmail)
            || string.IsNullOrWhiteSpace(parsed.PrivateKey)
            || string.IsNullOrWhiteSpace(parsed.ProjectId))
        {
            throw new FirebaseCustomTokenUnavailableException(
                "The configured service-account key file is missing required fields.");
        }

        // GUARD 4 — project pinning. The key must be for the project this gateway is
        // configured to mint for. A key for any other tenant fails closed here instead
        // of silently minting cross-tenant identities.
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new FirebaseCustomTokenUnavailableException(
                $"{FirebaseCustomTokenOptions.SectionName}:ProjectId is not configured.");
        }

        if (!string.Equals(parsed.ProjectId, _options.ProjectId, StringComparison.Ordinal))
        {
            throw new FirebaseCustomTokenUnavailableException(
                "The service-account key belongs to a different Firebase project than the "
                + "configured ProjectId. Refusing to mint cross-tenant tokens.");
        }

        RSAParameters parameters;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(parsed.PrivateKey);
            parameters = rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new FirebaseCustomTokenUnavailableException(
                "The service-account private key could not be parsed.");
        }

        _key = parsed;
        _rsaParameters = parameters;

        _logger.LogInformation(
            "Firebase chat custom-token minting enabled for project {ProjectId}.",
            parsed.ProjectId);
    }

    private sealed class ServiceAccountKey
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("client_email")]
        public string ClientEmail { get; set; } = string.Empty;

        [JsonPropertyName("private_key")]
        public string PrivateKey { get; set; } = string.Empty;
    }
}
